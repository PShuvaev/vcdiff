﻿using System;
using System.IO;
using System.Threading.Tasks;
using VCDiff.Includes;
using VCDiff.Shared;

namespace VCDiff.Encoders
{
    /// <summary>
    /// A simple VCDIFF Encoder class.
    /// </summary>
    public class VcEncoder : IDisposable
    {
        private ByteBuffer? oldData;
        private readonly ByteStreamReader targetData;
        private readonly Stream outputStream;
        private readonly Stream sourceStream;
        private readonly RollingHash hasher;
        private readonly int bufferSize;

        private static readonly byte[] MagicBytes = { 0xD6, 0xC3, 0xC4, 0x00, 0x00 };
        private static readonly byte[] MagicBytesExtended = { 0xD6, 0xC3, 0xC4, (byte)'S', 0x00 };
        private readonly int blockSize;
        private readonly int chunkSize;
        private readonly bool disposeRollingHash = false;

        /// <summary>
        /// Creates a new VCDIFF Encoder. The input streams will not be closed once this object is disposed.
        /// </summary>
        /// <param name="source">The dictionary (sourceStream file).</param>
        /// <param name="target">The target to create the diff from.</param>
        /// <param name="outputStream">The stream to write the diff into.</param>
        /// <param name="maxBufferSize">The maximum buffer size for window chunking in megabytes (MiB).</param>
        /// <param name="blockSize">
        /// The block size to use. Must be a power of two. No match smaller than this block size will be identified.
        /// Increasing blockSize by a factor of two will halve the amount of memory needed for the next block table, and will halve the setup time
        /// for a new BlockHash.  However, it also doubles the minimum match length that is guaranteed to be found.
        /// 
        /// Blocksizes that are n mod 32 = 0 are AVX2 accelerated. Blocksizes that are n mod 16 = 0 are SSE2 accelerated, if supported. 16 is a good default
        /// for most scenarios, but you should use a block size of 32 or 64 for very similar data, or to optimize for speed.
        /// </param>
        /// <param name="chunkSize">
        /// The minimum size of a string match that is worth putting into a COPY. This must be bigger than twice the block size.</param>
        /// <param name="rollingHash">
        /// Manually provide a <see cref="RollingHash"/> instance that can be reused for multiple encoding instances
        /// of the same block size.
        ///
        /// If you provide a <see cref="RollingHash"/> instance, you must dispose of it yourself.
        /// </param>
        /// <exception cref="ArgumentException">If an invalid blockSize or chunkSize is used..</exception>
        public VcEncoder(Stream source, Stream target, Stream outputStream,
            int maxBufferSize = 1, int blockSize = 16, int chunkSize = 0, RollingHash? rollingHash = null)
        {
            if (maxBufferSize <= 0) maxBufferSize = 1;
            this.blockSize = blockSize;
            this.chunkSize = chunkSize < 2 ? this.blockSize * 2 : chunkSize;
            this.sourceStream = source;
            this.targetData = new ByteStreamReader(target);
            this.outputStream = outputStream;
            if (rollingHash == null)
            {
                this.disposeRollingHash = true;
                this.hasher = new RollingHash(this.blockSize);
            }
            else
            {
                this.hasher = rollingHash;
            }
            if (this.hasher.WindowSize != this.blockSize)
            {
                throw new ArgumentException("Supplied RollingHash instance has a different window size than blocksize!");
            }
            this.bufferSize = maxBufferSize * 1024 * 1024;

            if (this.blockSize % 2 != 0 || this.chunkSize < 2 || this.chunkSize < 2 * this.blockSize)
            {
                throw new ArgumentException($"{this.blockSize} can not be less than 2 or twice the blocksize of the dictionary {this.blockSize}.");
            }

        }

        /// <summary>
        /// Calculate and write a diff for the file.
        /// </summary>
        /// <param name="interleaved">Whether to output in SDCH interleaved diff format.</param>
        /// <param name="checksumFormat">
        /// Whether to include Adler32 checksums for encoded data windows. If interleaved is true, <see cref="ChecksumFormat.Xdelta3"/>
        /// is not supported.
        /// </param>
        /// <returns>
        /// <see cref="VCDiffResult.SUCCESS"/> if successful, <see cref="VCDiffResult.ERROR"/> if the sourceStream or target are zero-length.</returns>
        /// <exception cref="ArgumentException">If interleaved is true, and <see cref="ChecksumFormat.Xdelta3"/> is chosen.</exception>
        public VCDiffResult Encode(bool interleaved = false, 
            ChecksumFormat checksumFormat = ChecksumFormat.None)
        {
            if (oldData == null)
            {
                this.oldData = new ByteBuffer(sourceStream);
            }

            if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
            {
                throw new ArgumentException("Interleaved diffs can not have an xdelta3 checksum!");
            }

            if (targetData.Length == 0 || oldData.Length == 0)
            {
                return VCDiffResult.ERROR;
            }

            VCDiffResult result = VCDiffResult.SUCCESS;

            oldData.Position = 0;
            targetData.Position = 0;

            // file header
            // write magic bytes
            if (!interleaved && checksumFormat != ChecksumFormat.SDCH)
            {
                outputStream.Write(MagicBytes);
            }
            else
            {
                outputStream.Write(MagicBytesExtended);
            }

            //read in all the dictionary it is the only thing that needs to be
            BlockHash dictionary = new BlockHash(oldData, 0, hasher, blockSize);
            dictionary.AddAllBlocks();
            oldData.Position = 0;

            ChunkEncoder chunker = new ChunkEncoder(dictionary, oldData, hasher, checksumFormat, interleaved, chunkSize);
            Memory<byte> buf = new Memory<byte>(new byte[bufferSize]);
            Span<byte> bufSpan = buf.Span;

            while (targetData.CanRead)
            {
                int bytesRead = targetData.ReadBytesIntoBuf(bufSpan);
                using ByteBuffer ntarget = new ByteBuffer(buf[..bytesRead]);
                chunker.EncodeChunk(ntarget, outputStream);
            }

            return result;
        }

        /// <summary>
        /// Calculate and write a diff for the file.
        ///
        /// This method is only asynchronous for the initial buffering of the sourceStream into memory.
        /// Writing to the output stream will still occur synchronously. 
        /// It is recommended you use the synchronous <see cref="Encode"/> method for most use cases.
        /// </summary>
        /// <param name="interleaved">Whether to output in SDCH interleaved diff format.</param>
        /// <param name="checksumFormat">
        /// Whether to include Adler32 checksums for encoded data windows. If interleaved is true, <see cref="ChecksumFormat.Xdelta3"/>
        /// is not supported.
        /// </param>
        /// <returns>
        /// <see cref="VCDiffResult.SUCCESS"/> if successful, <see cref="VCDiffResult.ERROR"/> if the sourceStream or target are zero-length.</returns>
        /// <exception cref="ArgumentException">If interleaved is true, and <see cref="ChecksumFormat.Xdelta3"/> is chosen.</exception>
        public async Task<VCDiffResult> EncodeAsync(bool interleaved = false,
            ChecksumFormat checksumFormat = ChecksumFormat.None)
        {
            if (oldData == null)
            {
                this.oldData = await ByteBuffer.CreateBufferAsync(sourceStream);
            }

            if (interleaved && checksumFormat == ChecksumFormat.Xdelta3)
            {
                throw new ArgumentException("Interleaved diffs can not have an xdelta3 checksum!");
            }

            if (targetData.Length == 0 || oldData.Length == 0)
            {
                return VCDiffResult.ERROR;
            }

            VCDiffResult result = VCDiffResult.SUCCESS;

            oldData.Position = 0;
            targetData.Position = 0;

            // file header
            // write magic bytes
            if (!interleaved && checksumFormat != ChecksumFormat.SDCH)
            {
                await outputStream.WriteAsync(MagicBytes);
            }
            else
            {
                await outputStream.WriteAsync(MagicBytesExtended);
            }

            //read in all the dictionary it is the only thing that needs to be
            BlockHash dictionary = new BlockHash(oldData, 0, hasher, blockSize);
            dictionary.AddAllBlocks();
            oldData.Position = 0;

            Memory<byte> buf = new Memory<byte>(new byte[bufferSize]);
            ChunkEncoder chunker = new ChunkEncoder(dictionary, oldData, hasher, checksumFormat, interleaved, chunkSize);
            while (targetData.CanRead)
            {
                int read = targetData.ReadBytesIntoBuf(buf.Span);
                using ByteBuffer ntarget = new ByteBuffer(buf[..read]);
                chunker.EncodeChunk(ntarget, outputStream);
            }

            return result;
        }


        /// <summary>
        /// Disposes the encoder.
        /// </summary>
        public void Dispose()
        {
            oldData?.Dispose();
            if (this.disposeRollingHash)
            {
                this.hasher.Dispose();
            }
        }
    }
}