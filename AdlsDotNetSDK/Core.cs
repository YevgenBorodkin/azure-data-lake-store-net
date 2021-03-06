﻿using Microsoft.Azure.DataLake.Store.Acl;
using Microsoft.Azure.DataLake.Store.Serialization;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.DataLake.Store
{
    /// <summary>
    /// Structure containing byte array, offset, and length of data in byte array
    /// </summary>
    internal struct ByteBuffer
    {
        internal byte[] Data;
        internal int Offset;
        internal int Count;

        internal ByteBuffer(byte[] data, int offset, int count)
        {
            Data = data;
            Offset = offset;
            Count = count;
        }
    }
    /// <summary>
    /// Core is a stateless class. It contains thread safe methods for REST APIs. For each rest api command it sends a HTTP request to server. 
    /// Every API is threadsafe with some exceptions in Create and Append (Listed in the documentation of the respective apis).
    /// We have both async and sync versions of CREATE, APPEND, OPEN, CONCURRENTAPPEND. The reason we have that is if the application is doing these operations heavily using explicit threads,
    /// then using async-await internally creates unecessary threads in threadpool and performance degrades due to context switching. Application can create explicit threads in cases of uploader and downloader.
    /// All these operation also call sync versions of MakeCall in WebTransport layer.
    /// </summary>
    public class Core
    {
        /// <summary>
        /// Creates a directory. 
        /// </summary>
        /// <param name="path">Path of the directory</param>
        /// <param name="octalPermission">Octal Permission</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>true if it creates the directory else false</returns>
        public static async Task<bool> MkdirsAsync(string path, string octalPermission, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!string.IsNullOrEmpty(octalPermission))
            {
                if (!IsValidOctal(octalPermission))
                {
                    resp.IsSuccessful = false;
                    resp.Error = "Octal Permission not valid";
                    return false;
                }
                qp.Add("permission", Convert.ToString(octalPermission));
            }
            var responseTuple = await WebTransport.MakeCallAsync("MKDIRS", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return false;
            bool readerValue = false;
            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        using (StreamReader stReader = new StreamReader(stream))
                        {
                            using (var jsonReader = new JsonTextReader(stReader))
                            {
                                jsonReader.Read(); //Start Object
                                jsonReader.Read(); //"boolean"
                                jsonReader.Read(); //Value
                                readerValue = (bool)jsonReader.Value;

                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return false;
                }
            }
            return readerValue;
        }
        /// <summary>
        /// Checks if the octal permission string is a valid string
        /// </summary>
        /// <param name="octalPermission">Octal permission string</param>
        /// <returns>Returns true if it is a valid permission string else false</returns>
        internal static bool IsValidOctal(string octalPermission)
        {
            return Regex.IsMatch(octalPermission, "^[01]?[0-7]?[0-7]?[0-7]$");
        }
        /// <summary>
        /// Create a new file. This is an asynchronous operation.
        /// 
        /// Not threadsafe when CreateAsync is called multiple times for same path with different leaseId. 
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="overwrite">Overwrites the existing file if the flag is true</param>
        /// <param name="octalPermission">Octal permission string</param>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="createParent">If true creates any non-existing parent directories</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task CreateAsync(string path, bool overwrite, string octalPermission, string leaseId, string sessionId, bool createParent, SyncFlag flag, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForCreate(overwrite, octalPermission, leaseId, sessionId, createParent, flag, qp, resp))
            {
                return;
            }
            await WebTransport.MakeCallAsync("CREATE", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Create a new file. This is a synchronous operation.
        /// 
        /// Not threadsafe when Create is called for same path from different threads with different leaseId. 
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="overwrite">Overwrites the existing file if the flag is true</param>
        /// <param name="octalPermission">Octal permission string</param>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="createParent">If true creates any non-existing parent directories</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        public static void Create(string path, bool overwrite, string octalPermission, string leaseId, string sessionId, bool createParent, SyncFlag flag, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp)
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForCreate(overwrite, octalPermission, leaseId, sessionId, createParent, flag, qp, resp))
            {
                return;
            }
            WebTransport.MakeCall("CREATE", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp);
        }
        /// <summary>
        /// Sets the queryparams for create operation.
        /// </summary>
        /// <param name="overwrite">Overwrites the existing file if the flag is true</param>
        /// <param name="octalPermission">Octal permission string</param>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="createParent">If true creates any non-existing parent directories</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="qp">QueryParams</param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <returns>True if operationresponse is set correctly else false</returns>
        private static bool SetQueryParamForCreate(bool overwrite, string octalPermission, string leaseId, string sessionId, bool createParent, SyncFlag flag, QueryParams qp, OperationResponse resp)
        {
            if (!string.IsNullOrEmpty(octalPermission))
            {
                if (!IsValidOctal(octalPermission))
                {
                    resp.IsSuccessful = false;
                    resp.Error = "Octal Permission not valid";
                    return false;
                }
                qp.Add("permission", Convert.ToString(octalPermission));
            }
            qp.Add("overwrite", Convert.ToString(overwrite));
            if (!string.IsNullOrEmpty(leaseId))
            {
                qp.Add("leaseid", leaseId);
            }
            if (!string.IsNullOrEmpty(sessionId))
            {
                qp.Add("filesessionid", sessionId);
            }
            qp.Add("CreateParent", Convert.ToString(createParent));
            qp.Add("write", "true");//Suppress redirect
            qp.Add("syncFlag", Enum.GetName(typeof(SyncFlag), flag));
            return true;
        }
        /// <summary>
        /// Append data to file. This is an asynchronous operation.
        /// 
        /// Not threadsafe when AppendAsync is called for same path from different threads. 
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="offsetFile">Offset in the file at which data will be appended</param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task AppendAsync(string path, string leaseId, string sessionId, SyncFlag flag, long offsetFile, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForAppend(leaseId, sessionId, flag, offsetFile, qp, resp))
            {
                return;
            }
            await WebTransport.MakeCallAsync("APPEND", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Append data to file. This is a synchronous operation. 
        /// 
        /// Not threadsafe when Append is called for same path from different threads. 
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="offsetFile">Offset in the file at which data will be appended</param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        public static void Append(string path, string leaseId, string sessionId, SyncFlag flag, long offsetFile, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp)
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForAppend(leaseId, sessionId, flag, offsetFile, qp, resp))
            {
                return;
            }
            WebTransport.MakeCall("APPEND", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp);
        }
        /// <summary>
        /// Sets the queryparams for create operation.
        /// </summary>
        /// <param name="leaseId">String containing the lease ID, when a client obtains a lease on a file no other client can make edits to the file </param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="flag">Pass SyncFlag.DATA when writing bytes of data
        ///                    Pass SyncFlag.METADATA when metadata of the file like length, modified instant needs to be updated to be consistent
        ///                    with the actual data of file. After passing SyncFlag.METADATA GetFileStatus and ListStatus returns consistent data.
        ///                    Pass SyncFlag.CLOSE when no more data needs to be appended, file metadata is updated, lease is released and the stream is closed  </param>
        /// <param name="offsetFile">Offset in the file at which data will be appended</param>
        /// <param name="qp">QueryParams</param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <returns></returns>
        private static bool SetQueryParamForAppend(string leaseId, string sessionId, SyncFlag flag, long offsetFile, QueryParams qp, OperationResponse resp)
        {
            if (offsetFile < 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Offset of file is negative";
                return false;
            }
            if (!string.IsNullOrEmpty(leaseId))
            {
                qp.Add("leaseid", leaseId);
            }
            if (!string.IsNullOrEmpty(sessionId))
            {
                qp.Add("filesessionid", sessionId);
            }

            qp.Add("append", "true");//Avoid redirect
            qp.Add("offset", Convert.ToString(offsetFile));

            qp.Add("syncFlag", Enum.GetName(typeof(SyncFlag), flag));
            return true;
        }
        /// <summary>
        /// Performs concurrent append asynchronously at server. The offset at which append will occur is determined by server
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="autoCreate"></param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task ConcurrentAppendAsync(string path, bool autoCreate, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (autoCreate)
            {
                qp.Add("appendMode", "autocreate");
            }
            await WebTransport.MakeCallAsync("CONCURRENTAPPEND", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Performs concurrent append synchronously at server. The offset at which append will occur is determined by server
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="autoCreate"></param>
        /// <param name="dataBytes">Array of bytes to write to the file</param>
        /// <param name="offset">Offset in the byte array</param>
        /// <param name="length">Number of bytes to write from the offset</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        public static void ConcurrentAppend(string path, bool autoCreate, byte[] dataBytes, int offset, int length, AdlsClient client, RequestOptions req, OperationResponse resp)
        {
            QueryParams qp = new QueryParams();
            if (autoCreate)
            {
                qp.Add("appendMode", "autocreate");
            }
            WebTransport.MakeCall("CONCURRENTAPPEND", path, new ByteBuffer(dataBytes, offset, length), default(ByteBuffer), qp, client, req, resp);
        }
        /// <summary>
        /// Reads a file from server. This is an asynchronous operation.
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="offsetFile">Offset in the file at which data will be read from</param>
        /// <param name="buffer"> Buffer where data read will be stored</param>
        /// <param name="offset">Offset in buffer where data will be read</param>
        /// <param name="lengthFile">Length of the data to be read</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>Number of bytes read</returns>
        public static async Task<int> OpenAsync(string path, string sessionId, long offsetFile, byte[] buffer, int offset, int lengthFile, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForOpen(sessionId, offsetFile, lengthFile, qp, resp))
            {
                return 0;
            }
            var responseTuple = await WebTransport.MakeCallAsync("OPEN", path, default(ByteBuffer), new ByteBuffer(buffer, offset, lengthFile), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            return responseTuple?.Item2 ?? 0;
        }
        /// <summary>
        /// Reads a file from server. This is synchronous operation.
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="offsetFile">Offset in the file at which data will be read from</param>
        /// <param name="buffer"> Buffer where data read will be stored</param>
        /// <param name="offset">Offset in buffer where data will be read</param>
        /// <param name="lengthFile">Length of the data to be read</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <returns>Number of bytes read</returns>
        public static int Open(string path, string sessionId, long offsetFile, byte[] buffer, int offset, int lengthFile, AdlsClient client, RequestOptions req, OperationResponse resp)
        {
            QueryParams qp = new QueryParams();
            if (!SetQueryParamForOpen(sessionId, offsetFile, lengthFile, qp, resp))
            {
                return 0;
            }
            var responseTuple = WebTransport.MakeCall("OPEN", path, default(ByteBuffer), new ByteBuffer(buffer, offset, lengthFile), qp, client, req, resp);
            return responseTuple?.Item2 ?? 0;
        }
        /// <summary>
        /// Sets the queryparams for Open.
        /// </summary>
        /// <param name="sessionId">UUID that is used to obtain the file handler (stream) easily at server</param>
        /// <param name="offsetFile">Offset in the file at which data will be read from</param>
        /// <param name="lengthFile">Length of the data to be read</param>
        /// <param name="qp">QueryParams</param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <returns>True if the queryparams are set else false</returns>
        private static bool SetQueryParamForOpen(string sessionId, long offsetFile, int lengthFile, QueryParams qp, OperationResponse resp)
        {
            if (offsetFile < 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Offset of file is negative";
                return false;
            }
            if (lengthFile < 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Length of file is negative";
                return false;
            }
            qp.Add("read", "true");//Avoid redirect
            if (!string.IsNullOrEmpty(sessionId))
            {
                qp.Add("filesessionid", sessionId);
            }
            qp.Add("offset", Convert.ToString(offsetFile));
            qp.Add("length", Convert.ToString(lengthFile));
            return true;
        }
        /// <summary>
        /// Deletes a file or directory
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="recursive"></param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>True if delete is successful</returns>
        public static async Task<bool> DeleteAsync(string path, bool recursive, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            bool isSuccessful = false;
            QueryParams qp = new QueryParams();
            qp.Add("recursive", Convert.ToString(recursive));
            var responseTuple = await WebTransport.MakeCallAsync("DELETE", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return false;

            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        using (StreamReader stReader = new StreamReader(stream))
                        {
                            using (var jsonReader = new JsonTextReader(stReader))
                            {
                                jsonReader.Read(); //Start object {
                                jsonReader.Read(); //"boolean"
                                if (((string)jsonReader.Value).Equals("boolean"))
                                {
                                    jsonReader.Read();
                                    isSuccessful = (bool)jsonReader.Value;
                                }
                                jsonReader.Read(); //End object}
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return false;
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "The request was successful but output was null";
                return false;
            }

            return isSuccessful;
        }
        /// <summary>
        /// Renames a path.
        /// For renaming directory: If the destination exists then it puts the source directory one level under the destination.
        /// </summary>
        /// <param name="path">Path of the source file or directory</param>
        /// <param name="destination">Destination path</param>
        /// <param name="overwrite">For file: If true then overwrites the destination file if it exists 
        ///                         For directory: If the destination directory exists, then this flag has no use. Because it puts the source one level under destination.
        ///                                        If there is a subdirectory with same name as source one level under the destination path, this flag has no use. Rename fails  </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>True if rename is successful else false</returns>
        public static async Task<bool> RenameAsync(string path, string destination, bool overwrite, AdlsClient client,
            RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            bool isSuccessful = false;
            if (string.IsNullOrEmpty(destination))
            {
                resp.IsSuccessful = false;
                resp.Error = "Destination path is null";
                return false;
            }
            QueryParams qp = new QueryParams();
            qp.Add("destination", destination);
            if (overwrite) qp.Add("renameoptions", "overwrite");
            var responseTuple = await WebTransport.MakeCallAsync("RENAME", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return false;
            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        using (StreamReader stReader = new StreamReader(stream))
                        {
                            using (var jsonReader = new JsonTextReader(stReader))
                            {
                                jsonReader.Read(); //Start object {
                                jsonReader.Read(); //"boolean"
                                if (((string)jsonReader.Value).Equals("boolean"))
                                {
                                    jsonReader.Read();
                                    isSuccessful = (bool)jsonReader.Value;
                                }
                                jsonReader.Read(); //End object}
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return false;
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "Output is not expected";
                return false;
            }
            return isSuccessful;
        }

        /// <summary>
        /// Concatenate source files to a destination file. By default it wont delete source directory
        /// </summary>
        /// <param name="path">Path of the destination</param>
        /// <param name="sourceFiles">List containing paths of the source files</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task ConcatAsync(string path, List<string> sourceFiles, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            await ConcatAsync(path, sourceFiles, false, client, req, resp, cancelToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Concatenate source files to a destination file
        /// </summary>
        /// <param name="path">Path of the destination</param>
        /// <param name="sourceFiles">List containing paths of the source files</param>
        /// <param name="deleteSourceDirectory">If true then deletes the source directory if all the files under it are concatenated</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task ConcatAsync(string path, List<string> sourceFiles, bool deleteSourceDirectory, AdlsClient client,
        RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (sourceFiles == null || sourceFiles.Count == 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "No source files to concatenate";
                return;
            }
            HashSet<string> hashSet = new HashSet<string>();//To check whether we have duplciate file names in the list
            Newtonsoft.Json.Linq.JArray jArray = new Newtonsoft.Json.Linq.JArray();
            foreach (var sourceFile in sourceFiles)
            {
                if (string.IsNullOrEmpty(sourceFile))
                {
                    resp.IsSuccessful = false;
                    resp.Error = "One of the Files to concatenate is empty";
                    return;
                }
                if (sourceFile.Equals(path))
                {
                    resp.IsSuccessful = false;
                    resp.Error = "One of the Files to concatenate has same path";
                    return;
                }
                if (hashSet.Contains(sourceFile))
                {
                    resp.IsSuccessful = false;
                    resp.Error = "One of the Files to concatenate is same as another file";
                    return;
                }

                jArray.Add(sourceFile);
            }
            Newtonsoft.Json.Linq.JObject jObject = new Newtonsoft.Json.Linq.JObject();
            jObject.Add("sources", jArray);
            byte[] body = Encoding.UTF8.GetBytes(jObject.ToString(Formatting.None));
            QueryParams qp = new QueryParams();
            if (deleteSourceDirectory)
            {
                qp.Add("deleteSourceDirectory", "true");
            }
            IDictionary<string, string> headers = new Dictionary<string, string>();
            headers.Add("Content-Type", "application/json");
            await WebTransport.MakeCallAsync("MSCONCAT", path, new ByteBuffer(body, 0, body.Length), default(ByteBuffer), qp, client, req, resp, cancelToken, headers).ConfigureAwait(false);
        }

        #region GetFileStatusApis
        /// <summary>
        /// Gets meta data like full path, type (file or directory), group, user, permission, length,last Access Time,last Modified Time, expiry time, acl Bit, replication Factor
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="userIdFormat">Way the user or group object will be represented</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <param name="getConsistentFileLength"> True if we want to get updated length.</param>
        /// <returns>Returns the metadata of the file or directory</returns>
        public static async Task<DirectoryEntry> GetFileStatusAsync(string path, UserGroupRepresentation? userIdFormat, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken), bool getConsistentFileLength=false)
        {
            var getfileStatusResult = await GetFileStatusAsync<DirectoryEntryResult<DirectoryEntry>>(path,userIdFormat, null, client, req, resp, getConsistentFileLength, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful)
            {
                return null;
            }
            if (getfileStatusResult != null && getfileStatusResult.FileStatus != null)
            {
                getfileStatusResult.FileStatus.Name = string.IsNullOrEmpty(getfileStatusResult.FileStatus.Name) ? GetFileName(getfileStatusResult.FileStatus.Name) : getfileStatusResult.FileStatus.Name;
                getfileStatusResult.FileStatus.FullName = string.IsNullOrEmpty(getfileStatusResult.FileStatus.FullName) ? path : path + "/" + getfileStatusResult.FileStatus.FullName;
            }
            else
            {
                //This should never come here
                resp.IsSuccessful = false;
                resp.Error = $"Unexpected problem with parsing JSON output.";
                return null;
            }
            return getfileStatusResult?.FileStatus;
        }

        /// <summary>
        /// GetFilestatus api where the the type can be generic. The FullName of the output will not be populated.
        /// Caller of this API needs to update that
        /// </summary>
        /// <typeparam name="T">Type of the json deserialized</typeparam>
        /// <param name="path">Path of the directory</param>
        /// <param name="userIdFormat">Way the user or group object will be represented</param>
        /// <param name="extraQueryParams">Extra query parameters</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="getConsistentFileLength"> True if we want to get consistent and updated length</param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns></returns>

        internal static async Task<T> GetFileStatusAsync<T>(string path, UserGroupRepresentation? userIdFormat, IDictionary<string, string> extraQueryParams, AdlsClient client, RequestOptions req, OperationResponse resp, bool getConsistentFileLength = false, CancellationToken cancelToken = default(CancellationToken)) where T : class
        {
            QueryParams qp = new QueryParams();
            userIdFormat = userIdFormat ?? UserGroupRepresentation.ObjectID;
            qp.Add("tooid", Convert.ToString(userIdFormat == UserGroupRepresentation.ObjectID));
            if (getConsistentFileLength)
            {
                qp.Add("getconsistentlength", "true");
            }
            if (extraQueryParams != null)
            {
                foreach (var key in extraQueryParams.Keys)
                {
                    qp.Add(key, extraQueryParams[key]);
                }
            }

            var responseTuple = await WebTransport.MakeCallAsync("GETFILESTATUS", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return null;
            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        return JsonCustomConvert.DeserializeObject<T>(stream, new Newtonsoft.Json.JsonSerializerSettings());
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return null;
                }

            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "Output is not expected";
                return null;
            }
        }
        #endregion

        /// <summary>
        /// API copied from Path.GetFileName (https://referencesource.microsoft.com/#mscorlib/system/io/path.cs,95facc58d06cadd0)
        /// Prevents the Invalid Char check because hadoop supports some of those characters.
        /// </summary>
        /// <returns></returns>
        internal static string GetFileName(string path)
        {
            if (path != null)
            {
                for (int i = path.Length; --i >= 0;)
                {
                    char ch = path[i];
                    if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar || ch == Path.VolumeSeparatorChar)
                        return path.Substring(i + 1, path.Length - i - 1);

                }
            }
            return path;
        }

        #region ListStatusApis

        /// <summary>
        /// Lists the sub-directories or files contained in a directory
        /// </summary>
        /// <param name="path">Path of the directory</param>
        /// <param name="listAfter">Filename after which list of files should be obtained from server</param>
        /// <param name="listBefore">Filename till which list of files should be obtained from server</param>
        /// <param name="listSize">List size to obtain from server</param>
        /// <param name="userIdFormat">Way the user or group object will be represented</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>List of directoryentries</returns>
        public static async Task<List<DirectoryEntry>> ListStatusAsync(string path, String listAfter, String listBefore, int listSize, UserGroupRepresentation? userIdFormat, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            return await ListStatusAsync(path, listAfter, listBefore, listSize, userIdFormat, Selection.Standard, client, req, resp, cancelToken);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="path">Path of the directory</param>
        /// <param name="listAfter">Filename after which list of files should be obtained from server</param>
        /// <param name="listBefore">Filename till which list of files should be obtained from server</param>
        /// <param name="listSize">List size to obtain from server</param>
        /// <param name="userIdFormat">Way the user or group object will be represented. Won't be honored for Selection.Minimal</param>
        /// <param name="selection">Define data to return for each entry</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>List of directoryentries</returns>
        /// <returns></returns>
        internal static async Task<List<DirectoryEntry>> ListStatusAsync(string path, String listAfter, String listBefore, int listSize, UserGroupRepresentation? userIdFormat, Selection selection, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            var getListStatusResult = await ListStatusAsync<DirectoryEntryListResult<DirectoryEntry>>(path, listAfter, listBefore, listSize, userIdFormat, selection, null, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful)
            {
                return null;
            }
            return GetDirectoryEntryListWithFullPath<DirectoryEntry>(path, getListStatusResult, resp);
        }

        /// <summary>
        /// Populates the fullname of the directoryentry returned by the liststatus
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path"></param>
        /// <param name="getListStatusResult"></param>
        /// <param name="resp"></param>
        /// <returns></returns>
        internal static List<T> GetDirectoryEntryListWithFullPath<T>(string path, DirectoryEntryListResult<T> getListStatusResult, OperationResponse resp) where T : DirectoryEntry
        {
            if (getListStatusResult != null && getListStatusResult.FileStatuses != null && getListStatusResult.FileStatuses.FileStatus != null)
            {
                string suffixedPath = path.EndsWith("/") ? path : path + "/";
                foreach (var entry in getListStatusResult.FileStatuses.FileStatus)
                {
                    entry.FullName = string.IsNullOrEmpty(entry.Name) ? path : suffixedPath + entry.Name;
                }
                return getListStatusResult.FileStatuses.FileStatus;
            }
            else
            {
                //This should never come here
                resp.IsSuccessful = false;
                resp.Error = $"Unexpected problem with parsing JSON output.";
                return null;
            }
        }

        /// <summary>
        /// ListStatus api where the the type can be generic. The FullName of the output will not be populated.
        /// Caller of this API needs to update that
        /// </summary>
        /// <typeparam name="T">Type of the json deserialized</typeparam>
        /// <param name="path">Path of the directory</param>
        /// <param name="listAfter">Filename after which list of files should be obtained from server</param>
        /// <param name="listBefore">Filename till which list of files should be obtained from server</param>
        /// <param name="listSize">List size to obtain from server</param>
        /// <param name="userIdFormat">Way the user or group object will be represented. Won't be honored for Selection.Minimal</param>
        /// <param name="selection">Define data to return for each entry</param>
        /// <param name="extraQueryParams">Dictionary containing extra query params</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>List of directoryentries</returns>
        /// <returns></returns>
        internal static async Task<T> ListStatusAsync<T>(string path, String listAfter, String listBefore, int listSize, UserGroupRepresentation? userIdFormat, Selection selection, IDictionary<string, string> extraQueryParams, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken)) where T : class
        {
            QueryParams qp = new QueryParams();
            if (!string.IsNullOrWhiteSpace(listAfter))
            {
                qp.Add("listAfter", listAfter);
            }
            if (!string.IsNullOrWhiteSpace(listBefore))
            {
                qp.Add("listBefore", listBefore);
            }
            if (listSize > 0)
            {
                qp.Add("listSize", Convert.ToString(listSize));
            }
            userIdFormat = userIdFormat ?? UserGroupRepresentation.ObjectID;
            
            if (selection != Selection.Minimal)
            {
                qp.Add("tooid", Convert.ToString(userIdFormat == UserGroupRepresentation.ObjectID));
            }

            if (selection != Selection.Standard)
            {
                qp.Add("select", selection.ToString());
            }

            if (extraQueryParams != null)
            {
                foreach (var key in extraQueryParams.Keys)
                {
                    qp.Add(key, extraQueryParams[key]);
                }
            }
            var responseTuple = await WebTransport.MakeCallAsync("LISTSTATUS", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return null;
            if (responseTuple != null)
            {
                try
                {

                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        return JsonCustomConvert.DeserializeObject<T>(stream, new Newtonsoft.Json.JsonSerializerSettings());
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "Output is not expected";
            }
            return null;

        }

        /// <summary>
        /// Lists the deleted streams or directories in the trash matching the hint.
        /// Caution: Undeleting files is a best effort operation.  There are no guarantees that a file can be restored once it is deleted. The use of this API is enabled via whitelisting. If your ADL account is not whitelisted, then using this api will throw Not immplemented exception. For further information and assistance please contact Microsoft support.
        /// </summary>
        /// <param name="hint">String to match</param>
        /// <param name="listAfter">Filename after which list of files should be obtained from server</param>
        /// <param name="numResults">Search is executed until we find numResults or search completes. Maximum allowed value for this param is 4000. The number of returned entries could be more or less than numResults.</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>List of deleted entries</returns>

        public static async Task<TrashStatus> EnumerateDeletedItemsAsync(string hint, string listAfter, int numResults, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!string.IsNullOrEmpty(hint) && !string.IsNullOrWhiteSpace(hint))
            {
                qp.Add("hint", hint);
            }
            else
            {
                throw new ArgumentException($"Hint cannot be skipped or be empty or a whitespace. Please provide a specific hint");
            }

            if (!string.IsNullOrWhiteSpace(listAfter))
            {
                qp.Add("listAfter", listAfter);
            }

            if (numResults > 4000 || numResults <= 0)
            {
                numResults = 4000;
            }

            qp.Add("listSize", Convert.ToString(numResults));
            qp.Add("api-version", "2018-08-01");

            var responseTuple = await WebTransport.MakeCallAsync("ENUMERATEDELETEDITEMS", "/", default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return null;
            if (responseTuple != null)
            {
                try
                {

                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        var trashStatus = JsonCustomConvert.DeserializeObject<TrashStatusResult>(stream, new JsonSerializerSettings()).TrashStatusRes;
                        if (trashStatus.TrashEntries != null && ((List<TrashEntry>)trashStatus.TrashEntries) != null)
                        {
                            trashStatus.NumFound = ((List<TrashEntry>)trashStatus.TrashEntries).Count;
                        }
                        return trashStatus;
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "Output is not expected";
            }
            return null;
        }

        /// <summary>
        /// Restore a stream or directory from trash to user space. This is a synchronous operation.
        /// Not threadsafe when Restore is called for same path from different threads. 
        /// Caution: Undeleting files is a best effort operation.  There are no guarantees that a file can be restored once it is deleted. The use of this API is enabled via whitelisting. If your ADL account is not whitelisted, then using this api will throw Not immplemented exception. For further information and assistance please contact Microsoft support.
        /// </summary>
        /// <param name="restoreToken">restore token of the entry to be restored. This is the trash directory path in enumeratedeleteditems response</param>
        /// <param name="restoreDestination">Path to which the entry should be restored</param>
        /// <param name="type">Type of the entry which is being restored</param>
        /// <param name="restoreAction">Action to take during destination name conflicts - overwrite or copy</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        public static Task RestoreDeletedItemsAsync(string restoreToken, string restoreDestination, string type, string restoreAction, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            if (!string.IsNullOrWhiteSpace(restoreToken))
            {
                qp.Add("restoreToken", restoreToken);
            }

            if (!string.IsNullOrWhiteSpace(restoreDestination))
            {
                qp.Add("restoreDestination", restoreDestination);
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                qp.Add("type", type);
            }

            if (!string.IsNullOrWhiteSpace(restoreAction))
            {
                qp.Add("restoreAction", restoreAction);
            }

            qp.Add("api-version", "2018-08-01");

            return WebTransport.MakeCallAsync("RESTOREDELETEDITEMS", "/", default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken);
        }

        #endregion

        /// <summary>
        /// Set the expiry time
        /// </summary>
        /// <param name="path">Path of the file</param>
        /// <param name="opt">Different type of expiry method for example: never expire, relative to now, etc that defines how to evaluate expiryTime</param>
        /// <param name="expiryTime">Expiry time value. It's interepreation depends on what ExpiryOption user passes</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task SetExpiryTimeAsync(string path, ExpiryOption opt, long expiryTime, AdlsClient client,
            RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            QueryParams qp = new QueryParams();
            qp.Add("expireTime", Convert.ToString(expiryTime));
            qp.Add("expiryOption", Enum.GetName(typeof(ExpiryOption), opt));
            await WebTransport.MakeCallAsync("SETEXPIRY", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks if the user/group has specified access of the given path
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="rwx">Permission to check in "rwx" string form. For example if the user wants to see if it has read, execute permission, the string would be r-x </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task CheckAccessSync(string path, string rwx, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(rwx))
            {
                resp.IsSuccessful = false;
                resp.Error = "RWX is empty";
                return;
            }
            if (!IsValidRwx(rwx))
            {
                resp.IsSuccessful = false;
                resp.Error = "RWX is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            qp.Add("fsaction", rwx);
            await WebTransport.MakeCallAsync("CHECKACCESS", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Sets the permission of the specified path
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="permission">Permission to check in unix octal form. For example if the user wants to see if owner has read, write execute permission, all groups has read write
        ///                          permission and others has read permission the string would be 741 </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task SetPermissionAsync(string path, string permission, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(permission))
            {
                resp.IsSuccessful = false;
                resp.Error = "permission is empty";
                return;
            }
            if (!IsValidOctal(permission))
            {
                resp.IsSuccessful = false;
                resp.Error = "permission is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            qp.Add("permission", permission);
            await WebTransport.MakeCallAsync("SETPERMISSION", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Sets the owner or/and group of the path
        /// </summary>
        /// <param name="path">Path of file or directory</param>
        /// <param name="user">Owner Id of the path</param>
        /// <param name="group">Group Id of the path</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns></returns>
        public static async Task SetOwnerAsync(string path, string user, string group, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            bool isUserInValid = string.IsNullOrWhiteSpace(user);
            bool isGroupInValid = string.IsNullOrWhiteSpace(group);
            if (isUserInValid && isGroupInValid)
            {
                resp.IsSuccessful = false;
                resp.Error = "User and group is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            if (!isUserInValid)
            {
                qp.Add("owner", user);
            }
            if (!isGroupInValid)
            {
                qp.Add("group", group);
            }
            await WebTransport.MakeCallAsync("SETOWNER", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Verifies whether the permission string is in rwx form
        /// </summary>
        /// <param name="rwx">permission string</param>
        /// <returns>True if it is in correct format else false</returns>
        private static bool IsValidRwx(string rwx)
        {
            return Regex.IsMatch(rwx, "^[r-][w-][x-]$");
        }
        /// <summary>
        /// Modifies acl entries of a file or directory with given ACL list. It merges the exisitng ACL list with given list.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpec">AclSpec string that contains the ACL entries delimited by comma </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task ModifyAclEntriesAsync(string path, string aclSpec, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(aclSpec))
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            qp.Add("aclspec", aclSpec);
            await WebTransport.MakeCallAsync("MODIFYACLENTRIES", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);

        }
        /// <summary>
        /// Modifies acl entries of a file or directory with given ACL list. It merges the exisitng ACL list with given list.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpecList">List of Acl Entries to modify</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task ModifyAclEntriesAsync(string path, List<AclEntry> aclSpecList, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (aclSpecList == null || aclSpecList.Count == 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification List is empty";
                return;
            }
            await ModifyAclEntriesAsync(path, AclEntry.SerializeAcl(aclSpecList, false), client, req, resp, cancelToken).ConfigureAwait(false);

        }
        /// <summary>
        /// Sets Acl Entries for a file or directory. It wipes out the existing Acl entries for the path.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpec">AclSpec string that contains the ACL entries delimited by comma </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task SetAclAsync(string path, string aclSpec, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(aclSpec))
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            qp.Add("aclspec", aclSpec);
            await WebTransport.MakeCallAsync("SETACL", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Sets Acl Entries for a file or directory. It wipes out the existing Acl entries for the path.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpecList">List of Acl Entries to set </param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task SetAclAsync(string path, List<AclEntry> aclSpecList, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (aclSpecList == null || aclSpecList.Count == 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification List is empty";
                return;
            }
            await SetAclAsync(path, AclEntry.SerializeAcl(aclSpecList, false), client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Removes specified Acl Entries for a file or directory.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpec">string containing Acl Entries to remove</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task RemoveAclEntriesAsync(string path, string aclSpec, AdlsClient client,
            RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(aclSpec))
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification is empty";
                return;
            }
            QueryParams qp = new QueryParams();
            qp.Add("aclspec", aclSpec);
            await WebTransport.MakeCallAsync("REMOVEACLENTRIES", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes specified Acl Entries for a file or directory.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="aclSpecList">List of Acl Entries to remove</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task RemoveAclEntriesAsync(string path, List<AclEntry> aclSpecList, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            if (aclSpecList == null || aclSpecList.Count == 0)
            {
                resp.IsSuccessful = false;
                resp.Error = "Acl Specification List is empty";
                return;
            }
            await RemoveAclEntriesAsync(path, AclEntry.SerializeAcl(aclSpecList, true), client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Removes all Acl Entries of AclScope default for a file or directory.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task RemoveDefaultAclAsync(string path, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            await WebTransport.MakeCallAsync("REMOVEDEFAULTACL", path, default(ByteBuffer), default(ByteBuffer), new QueryParams(), client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Removes all Acl Entries for a file or directory.
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        public static async Task RemoveAclAsync(string path, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            await WebTransport.MakeCallAsync("REMOVEACL", path, default(ByteBuffer), default(ByteBuffer), new QueryParams(), client, req, resp, cancelToken).ConfigureAwait(false);
        }
        /// <summary>
        /// Gets the ACL entry list, owner ID, group ID, octal permission and sticky bit (only for a directory) of the file/directory
        /// </summary>
        /// <param name="path">Path of the file or directory</param>
        /// <param name="userIdFormat">way to represent the user/group object</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>Acl information: ACL entry list, owner ID, group ID, octal permission and sticky bit</returns>
        public static async Task<AclStatus> GetAclStatusAsync(string path, UserGroupRepresentation? userIdFormat, AdlsClient client, RequestOptions req,
            OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            AclStatus status;
            QueryParams qp = new QueryParams();
            userIdFormat = userIdFormat ?? UserGroupRepresentation.ObjectID;
            qp.Add("tooid", Convert.ToString(userIdFormat == UserGroupRepresentation.ObjectID));
            var responseTuple = await WebTransport.MakeCallAsync("GETACLSTATUS", path, default(ByteBuffer), default(ByteBuffer), qp, client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return null;
            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        using (StreamReader stReader = new StreamReader(stream))
                        {
                            using (var jsonReader = new JsonTextReader(stReader))
                            {
                                List<AclEntry> entries = new List<AclEntry>();
                                string owner = "";
                                string group = "";
                                string permission = "";
                                bool stickyBit = false;
                                jsonReader.Read();//{ Start object
                                jsonReader.Read();//"AclStatus"
                                jsonReader.Read();//Start Object
                                do
                                {
                                    jsonReader.Read();
                                    if (jsonReader.TokenType.Equals(JsonToken.PropertyName))
                                    {
                                        if (((string)jsonReader.Value).Equals("entries"))
                                        {
                                            jsonReader.Read(); //Start of Array
                                            do
                                            {
                                                jsonReader.Read();
                                                if (!jsonReader.TokenType.Equals(JsonToken.EndArray))
                                                {
                                                    string entry = (string)jsonReader.Value;
                                                    entries.Add(AclEntry.ParseAclEntryString(entry, false));
                                                }
                                            } while (!jsonReader.TokenType.Equals(JsonToken.EndArray));
                                        }
                                        else if (((string)jsonReader.Value).Equals("owner"))
                                        {
                                            jsonReader.Read();
                                            owner = (string)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("group"))
                                        {
                                            jsonReader.Read();
                                            group = (string)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("permission"))
                                        {
                                            jsonReader.Read();
                                            permission = (string)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("stickyBit"))
                                        {
                                            jsonReader.Read();
                                            stickyBit = (bool)jsonReader.Value;
                                        }
                                    }
                                } while (!jsonReader.TokenType.Equals(JsonToken.EndObject));
                                status = new AclStatus(entries, owner, group, permission, stickyBit);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return null;
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "The request is successful but the response is null";
                return null;
            }
            return status;
        }
        /// <summary>
        /// Gets content summary of a file or directory
        /// </summary>
        /// <param name="path">Path of the directory or file</param>
        /// <param name="client">ADLS Client</param>
        /// <param name="req">Options to change behavior of the Http request </param>
        /// <param name="resp">Stores the response/ouput of the Http request </param>
        /// <param name="cancelToken">CancellationToken to cancel the request</param>
        /// <returns>ContentSummary of the path</returns>
        public static async Task<ContentSummary> GetContentSummaryAsync(string path, AdlsClient client, RequestOptions req, OperationResponse resp, CancellationToken cancelToken = default(CancellationToken))
        {
            ContentSummary summary;
            var responseTuple = await WebTransport.MakeCallAsync("GETCONTENTSUMMARY", path, default(ByteBuffer), default(ByteBuffer), new QueryParams(), client, req, resp, cancelToken).ConfigureAwait(false);
            if (!resp.IsSuccessful) return null;
            if (responseTuple != null)
            {
                try
                {
                    using (MemoryStream stream = new MemoryStream(responseTuple.Item1))
                    {
                        using (StreamReader stReader = new StreamReader(stream))
                        {
                            using (var jsonReader = new JsonTextReader(stReader))
                            {
                                long directoryCount = 0, fileCount = 0, length = 0, spaceConsumed = 0;
                                jsonReader.Read();//{
                                jsonReader.Read();//"ContentSummary"
                                jsonReader.Read();//{
                                do
                                {
                                    jsonReader.Read();
                                    if (jsonReader.TokenType.Equals(JsonToken.PropertyName))
                                    {
                                        if (((string)jsonReader.Value).Equals("directoryCount"))
                                        {
                                            jsonReader.Read();
                                            directoryCount = (long)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("fileCount"))
                                        {
                                            jsonReader.Read();
                                            fileCount = (long)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("length"))
                                        {
                                            jsonReader.Read();
                                            fileCount = (long)jsonReader.Value;
                                        }
                                        else if (((string)jsonReader.Value).Equals("spaceConsumed"))
                                        {
                                            jsonReader.Read();
                                            spaceConsumed = (long)jsonReader.Value;
                                        }
                                    }
                                } while (!jsonReader.TokenType.Equals(JsonToken.EndObject));
                                summary = new ContentSummary(directoryCount, fileCount, length, spaceConsumed);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    resp.IsSuccessful = false;
                    resp.Error = $"Unexpected problem with parsing JSON output. \r\nExceptionType: {ex.GetType()} \r\nExceptionMessage: {ex.Message}";
                    return null;
                }
            }
            else
            {
                resp.IsSuccessful = false;
                resp.Error = "";
                return null;
            }
            return summary;
        }
    }
}
