using System;
using System.Runtime.InteropServices;
using System.Text;
using DuetAPI;
using DuetAPI.Commands;
using DuetAPI.Utility;
using DuetControlServer.SPI.Communication.FirmwareRequests;
using DuetControlServer.SPI.Communication.Shared;

namespace DuetControlServer.SPI.Serialization
{
    /// <summary>
    /// Static class for reading data from SPI transmissions.
    /// It is expected that each data block occupies entire 4-byte blocks.
    /// Make sure to keep the data returned by these functions only as long as the underlying buffer is actually valid!
    /// </summary>
    public static class Reader
    {
        /// <summary>
        /// Read a packet header from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="packet">Read packet</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPacketHeader(ReadOnlySpan<byte> from, out PacketHeader packet)
        {
            packet = MemoryMarshal.Read<PacketHeader>(from);
            return Marshal.SizeOf<PacketHeader>();
        }

        /// <summary>
        /// Read a legacy config response from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="json">Config response JSON</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadLegacyConfigResponse(ReadOnlySpan<byte> from, out ReadOnlySpan<byte> json)
        {
            int jsonLength = MemoryMarshal.Read<ushort>(from);
            json = from.Slice(4, jsonLength);
            return 4 + jsonLength;
        }

        /// <summary>
        /// Read a code buffer update from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="bufferSpace">Buffer space</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeBufferUpdate(ReadOnlySpan<byte> from, out ushort bufferSpace)
        {
            CodeBufferUpdateHeader header = MemoryMarshal.Read<CodeBufferUpdateHeader>(from);
            bufferSpace = header.BufferSpace;
            return Marshal.SizeOf<CodeBufferUpdateHeader>();
        }

        /// <summary>
        /// Read a message from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="messageType">Message flags</param>
        /// <param name="reply">Raw message</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMessage(ReadOnlySpan<byte> from, out MessageTypeFlags messageType, out string reply)
        {
            MessageHeader header = MemoryMarshal.Read<MessageHeader>(from);
            int bytesRead = Marshal.SizeOf<MessageHeader>();

            // Read header
            messageType = header.MessageType;

            // Read message content
            if (header.Length > 0)
            {
                ReadOnlySpan<byte> unicodeReply = from.Slice(bytesRead, header.Length);
                reply = Encoding.UTF8.GetString(unicodeReply);
                bytesRead += header.Length;
            }
            else
            {
                reply = string.Empty;
            }
            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a macro file request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel that requested the execution</param>
        /// <param name="fromCode">Whether the macro request came from the G/M/T-code being executed</param>
        /// <param name="filename">Filename of the macro to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadMacroRequest(ReadOnlySpan<byte> from, out CodeChannel channel, out bool fromCode, out string filename)
        {
            ExecuteMacroHeader header = MemoryMarshal.Read<ExecuteMacroHeader>(from);
            int bytesRead = Marshal.SizeOf<ExecuteMacroHeader>();
 
            // Read header
            channel = header.Channel;
            fromCode = Convert.ToBoolean(header.FromCode);

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, header.Length);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read information about an abort file request 
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel running the file</param>
        /// <param name="abortAll">Whether all files are supposed to be aborted</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadAbortFile(ReadOnlySpan<byte> from, out CodeChannel channel, out bool abortAll)
        {
            AbortFileHeader header = MemoryMarshal.Read<AbortFileHeader>(from);
            channel = (CodeChannel)header.Channel;
            abortAll = header.AbortAll != 0;
            return Marshal.SizeOf<AbortFileHeader>();
        }

        /// <summary>
        /// Read a print pause event
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filePosition">Position at which the print has been paused</param>
        /// <param name="reason">Reason why the print has been paused</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadPrintPaused(ReadOnlySpan<byte> from, out uint filePosition, out PrintPausedReason reason)
        {
            PrintPausedHeader header = MemoryMarshal.Read<PrintPausedHeader>(from);
            filePosition = header.FilePosition;
            reason = (PrintPausedReason)header.PauseReason;
            return Marshal.SizeOf<PrintPausedHeader>();
        }

        /// <summary>
        /// Read a heightmap report
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="map">Deserialized heightmap</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadHeightMap(ReadOnlySpan<byte> from, out Heightmap map)
        {
            HeightMapHeader header = MemoryMarshal.Read<HeightMapHeader>(from);
            map = new Heightmap
            {
                XMin = header.XMin,
                XMax = header.XMax,
                XSpacing = header.XSpacing,
                YMin = header.YMin,
                YMax = header.YMax,
                YSpacing = header.YSpacing,
                Radius = header.Radius,
                NumX = header.NumX,
                NumY = header.NumY
            };

            int headerSize = Marshal.SizeOf<HeightMapHeader>(), dataLength = Marshal.SizeOf<float>() * map.NumX * map.NumY;
            if (from.Length > headerSize)
            {
                ReadOnlySpan<byte> zCoordinates = from.Slice(headerSize, dataLength);
                map.ZCoordinates = MemoryMarshal.Cast<byte, float>(zCoordinates).ToArray();
            }
            else
            {
                map.NumX = map.NumY = dataLength = 0;
                map.ZCoordinates = Array.Empty<float>();
            }
            return headerSize + dataLength;
        }

        /// <summary>
        /// Read a G-code channel
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Channel that has acquired the lock</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadCodeChannel(ReadOnlySpan<byte> from, out CodeChannel channel)
        {
            CodeChannelHeader header = MemoryMarshal.Read<CodeChannelHeader>(from);
            channel = header.Channel;
            return Marshal.SizeOf<CodeChannelHeader>();
        }

        /// <summary>
        /// Read a file chunk request`
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="filename">Filename to read from</param>
        /// <param name="offset">Offset in the file</param>
        /// <param name="maxLength">Maximum chunk length</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadFileChunkRequest(ReadOnlySpan<byte> from, out string filename, out uint offset, out uint maxLength)
        {
            FileChunkHeader header = MemoryMarshal.Read<FileChunkHeader>(from);
            int bytesRead = Marshal.SizeOf<FileChunkHeader>();

            // Read header
            offset = header.Offset;
            maxLength = header.MaxLength;

            // Read filename
            ReadOnlySpan<byte> unicodeFilename = from.Slice(bytesRead, (int)header.FilenameLength);
            filename = Encoding.UTF8.GetString(unicodeFilename);
            bytesRead += (int)header.FilenameLength;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a <see cref="Request.EvaluationResult"/> request
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="expression">Expression</param>
        /// <param name="result">Evaluation result</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadEvaluationResult(ReadOnlySpan<byte> from, out string expression, out object result)
        {
            EvaluationResultHeader header = MemoryMarshal.Read<EvaluationResultHeader>(from);
            int bytesRead = Marshal.SizeOf<EvaluationResultHeader>();

            // Read expression
            ReadOnlySpan<byte> unicodeExpression = from.Slice(bytesRead, header.ExpressionLength);
            expression = Encoding.UTF8.GetString(unicodeExpression);
            bytesRead += header.ExpressionLength;

            // Read value
            switch (header.Type)
            {
                case DataType.Int:
                    result = header.IntValue;
                    break;
                case DataType.UInt:
                    result = header.UIntValue;
                    break;
                case DataType.Float:
                    result = header.FloatValue;
                    break;
                case DataType.IntArray:
                    int[] intArray = new int[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        intArray[i] = MemoryMarshal.Read<int>(from[bytesRead..]);
                        bytesRead += Marshal.SizeOf<int>();
                    }
                    result = intArray;
                    break;
                case DataType.UIntArray:
                    uint[] uintArray = new uint[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        uintArray[i] = MemoryMarshal.Read<uint>(from[bytesRead..]);
                        bytesRead += Marshal.SizeOf<uint>();
                    }
                    result = uintArray;
                    break;
                case DataType.FloatArray:
                    float[] floatArray = new float[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        floatArray[i] = MemoryMarshal.Read<float>(from[bytesRead..]);
                        bytesRead += Marshal.SizeOf<float>();
                    }
                    result = floatArray;
                    break;
                case DataType.String:
                    result = Encoding.UTF8.GetString(from.Slice(bytesRead, header.IntValue));
                    bytesRead += header.IntValue;
                    break;
                case DataType.DriverId:
                    result = new DriverId(header.UIntValue);
                    break;
                case DataType.DriverIdArray:
                    DriverId[] driverIdArray = new DriverId[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        driverIdArray[i] = new DriverId(MemoryMarshal.Read<uint>(from[bytesRead..]));
                        bytesRead += Marshal.SizeOf<uint>();
                    }
                    result = driverIdArray;
                    break;
                case DataType.Bool:
                    result = Convert.ToBoolean(header.IntValue);
                    break;
                case DataType.BoolArray:
                    bool[] boolArray = new bool[header.IntValue];
                    for (int i = 0; i < header.IntValue; i++)
                    {
                        boolArray[i] = Convert.ToBoolean(MemoryMarshal.Read<byte>(from[bytesRead..]));
                        bytesRead += Marshal.SizeOf<byte>();
                    }
                    result = boolArray;
                    break;
                case DataType.Expression:
                    string errorMessage = Encoding.UTF8.GetString(from.Slice(bytesRead, header.IntValue));
                    result = new CodeParserException(errorMessage);
                    break;
                default:
                    result = null;
                    break;
            }

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a <see cref="Request.DoCode"/> request
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="channel">Code channel</param>
        /// <param name="code">Code to execute</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadDoCode(ReadOnlySpan<byte> from, out CodeChannel channel, out string code)
        {
            DoCodeHeader header = MemoryMarshal.Read<DoCodeHeader>(from);
            int bytesRead = Marshal.SizeOf<DoCodeHeader>();

            // Read header
            channel = header.Channel;

            // Read code
            ReadOnlySpan<byte> unicodeCode = from.Slice(bytesRead, header.Length);
            code = Encoding.UTF8.GetString(unicodeCode);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Read a UTF-8 encoded string request from a memory span
        /// </summary>
        /// <param name="from">Origin</param>
        /// <param name="data">UTF-8 string</param>
        /// <returns>Number of bytes read</returns>
        public static int ReadStringRequest(ReadOnlySpan<byte> from, out ReadOnlySpan<byte> data)
        {
            StringHeader header = MemoryMarshal.Read<StringHeader>(from);
            int bytesRead = Marshal.SizeOf<StringHeader>();

            // Read data
            data = from.Slice(bytesRead, header.Length);
            bytesRead += header.Length;

            return AddPadding(bytesRead);
        }

        /// <summary>
        /// Add padding to a number of read bytes to maintain alignment on a 4-byte boundary
        /// </summary>
        /// <param name="bytesRead">Number of bytes read</param>
        /// <returns>Aligned number of bytes</returns>
        private static int AddPadding(int bytesRead) => ((bytesRead + 3) / 4) * 4;
    }
}