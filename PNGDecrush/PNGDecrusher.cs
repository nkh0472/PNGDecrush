using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web;

namespace PNGDecrush
{
    public class PNGDecrusher
    {
        public static void Decrush(Stream input, Stream output)
        {
            using (MemoryStream fixedChunksOutput = new MemoryStream())
            {
                DecrushAtChunkLevel(input, fixedChunksOutput);
                DecrushAtPixelLevel(fixedChunksOutput, output);
            }
        }

        private static void DecrushAtChunkLevel(Stream input, Stream output)
        {
            IEnumerable<PNGChunk> fixedChunks = DecrushChunks(PNGChunkParser.ChunksFromStream(input));
            PNGChunkParser.WriteChunksAsPNG(fixedChunks, output);
        }

        public static IEnumerable<PNGChunk> DecrushChunks(IEnumerable<PNGChunk> chunks)
        {
            IEnumerable<PNGChunk> chunksWithoutAppleChunk = ChunksByRemovingAppleCgBIChunks(chunks);

            if (chunksWithoutAppleChunk.Count() == chunks.Count())
            {
                throw new InvalidDataException("Could not find a CgBI chunk. Image wasn't crushed with Apple's -iohone option.");
            }
            
            return ConvertIDATChunksFromDeflateToZlib(chunksWithoutAppleChunk);;
        }

        private static IEnumerable<PNGChunk> ConvertIDATChunksFromDeflateToZlib(IEnumerable<PNGChunk> inputChunks)
        {
            // Multiple IDAT chunks must be combined together to form a single DEFLATE payload.
            // This payload is recompressed with zlib headers intact, and then split up into chunks again

            IEnumerable<PNGChunk> idatChunks = inputChunks.Where(c => c.Type == PNGChunk.ChunkType.IDAT);
            byte[] zlibData = RecompressedZlibDataFromChunks(idatChunks);

            IEnumerable<PNGChunk> newIDATChunks = CreateIdatChunksFromData(zlibData, idatChunks.Count());

            return ReplaceOldIdatChunksWithNewChunks(inputChunks, newIDATChunks);
        }

        private static IEnumerable<PNGChunk> CreateIdatChunksFromData(byte[] data, int numberOfChunks)
        {
            IEnumerable<byte[]> dataChunks = SplitBufferIntoChunks(data, numberOfChunks);
            return dataChunks.Select(chunkData => IDATChunkWithBytes(chunkData));
        }

        private static byte[] RecompressedZlibDataFromChunks(IEnumerable<PNGChunk> idatChunks)
        {
            byte[] deflateData = CombinedDataFromChunks(idatChunks);
            return ConvertDeflateToZlib(deflateData);
        }

        public static IEnumerable<byte[]> SplitBufferIntoChunks(byte[] input, int numberOfChunks)
        {
            List<byte[]> result = new List<byte[]>();

            int bytesLeft = input.Length;
            int currentInputIndex = 0;

            for (int chunksLeft = numberOfChunks; chunksLeft > 0; chunksLeft--)
            {
                int maxChunkSize = (int)Math.Ceiling((double)bytesLeft / (double)chunksLeft);
                int thisChunkSize = Math.Min(maxChunkSize, bytesLeft);

                byte[] chunkData = SubArray(input, currentInputIndex, thisChunkSize);

                currentInputIndex += thisChunkSize;
                bytesLeft -= thisChunkSize;

                result.Add(chunkData);
            }

            if (currentInputIndex != input.Length || bytesLeft != 0)
            {
                throw new InvalidOperationException();
            }

            return result;
        }

        private static TType[] SubArray<TType>(TType[] input, int startIndex, int count)
        {
            TType[] result = new TType[count];
            Array.Copy(input, startIndex, result, 0, result.Length);
            return result;
        }

        private static IEnumerable<PNGChunk> ReplaceOldIdatChunksWithNewChunks(IEnumerable<PNGChunk> chunks, IEnumerable<PNGChunk> newIdatChunks)
        {
            int indexOfFirstIdat = chunks.Select((c, i) => new { index = i, chunk = c })
                                         .First(o => o.chunk.Type == PNGChunk.ChunkType.IDAT)
                                         .index;

            List<PNGChunk> result = chunks.Where(c => c.Type != PNGChunk.ChunkType.IDAT).ToList();
            result.InsertRange(indexOfFirstIdat, newIdatChunks);
            return result;
        }

        private static PNGChunk IDATChunkWithBytes(byte[] bytes)
        {
            string type = PNGChunk.StringFromType(PNGChunk.ChunkType.IDAT);
            return new PNGChunk(type, bytes, CalculateCRCForChunk(type, bytes));
        }

        private static byte[] CombinedDataFromChunks(IEnumerable<PNGChunk> chunks)
        {
            int totalLength = chunks.Select(c => c.Data.Length).Sum();

            byte[] result = new byte[totalLength];
            int bytesWritten = 0;

            foreach (PNGChunk chunk in chunks)
            {
                chunk.Data.CopyTo(result, bytesWritten);
                bytesWritten += chunk.Data.Length;
            }

            return result;
        }

        private static byte[] ConvertDeflateToZlib(byte[] input)
        {
            // Was Ionic.Zlib.ZlibStream (DotNetZip, unmaintained since 2011).
            // SharpZipLib's DeflaterOutputStream is a full RFC1950 (zlib) wrapper:
            // writes the 2-byte zlib header on first Write and the Adler-32 trailer on Finish().
            // Level 9 matches DotNetZip's former default (BestCompression) bit-for-bit in intent.
            using (MemoryStream deflateData = new MemoryStream(input))
            using (System.IO.Compression.DeflateStream deflateStream = new System.IO.Compression.DeflateStream(deflateData, System.IO.Compression.CompressionMode.Decompress))
            using (MemoryStream zlibData = new MemoryStream())
            {
                ICSharpCode.SharpZipLib.Zip.Compression.Deflater deflater =
                    new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(9);
                using (ICSharpCode.SharpZipLib.Zip.Compression.Streams.DeflaterOutputStream zlibStream =
                    new ICSharpCode.SharpZipLib.Zip.Compression.Streams.DeflaterOutputStream(zlibData, deflater))
                {
                    deflateStream.CopyTo(zlibStream);
                    zlibStream.Finish();
                }
                return zlibData.ToArray();
            }
        }

        private static IEnumerable<PNGChunk> ChunksByRemovingAppleCgBIChunks(IEnumerable<PNGChunk> chunks)
        {
            return chunks.Where(c => c.Type != PNGChunk.ChunkType.CgBI);
        }

        public static uint CalculateCRCForChunk(string chunkType, byte[] chunkData)
        {
            byte[] chunkTypeBytes = Encoding.UTF8.GetBytes(chunkType);

            // SharpZipLib CRC32 (was Ionic.Crc.CRC32) — same standard PNG CRC-32.
            ICSharpCode.SharpZipLib.Checksum.Crc32 crc32calculator = new ICSharpCode.SharpZipLib.Checksum.Crc32();
            crc32calculator.Update(chunkTypeBytes);
            crc32calculator.Update(chunkData);

            return (uint)crc32calculator.Value;
        }

        public static void DecrushAtPixelLevel(Stream pngInputStream, Stream outputStream)
        {
            using (Bitmap bitmap = new Bitmap(pngInputStream))
            {
                BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, bitmap.PixelFormat);
                {
                    FixPixelsInBitmapData(bitmapData);
                }
                bitmap.UnlockBits(bitmapData);

                bitmap.Save(outputStream, ImageFormat.Png);
            }
        }

        private static void FixPixelsInBitmapData(BitmapData bitmapData)
        {
            int totalBytes = bitmapData.Stride * bitmapData.Height;
            byte[] pixelData = new byte[totalBytes];

            bool hasAlpha = BitmapDataHasAlpha(bitmapData);
            uint bytesPerPixel = BytesPerPixelFromBitmapData(bitmapData);

            System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, pixelData, 0, pixelData.Length);
            {
                FixPixelsInBuffer(pixelData, hasAlpha, bytesPerPixel);
            }
            System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
        }

        private static void FixPixelsInBuffer(byte[] pixelData, bool hasAlpha, uint bytesPerPixel)
        {
            for (uint i = 0; i < pixelData.Length; i += bytesPerPixel)
            {
                ReverseRGBtoBGRByteSwap(pixelData, i);

                if (hasAlpha)
                {
                    ReversePremultipliedAlpha(pixelData, i);
                }
            }
        }

        private static void ReverseRGBtoBGRByteSwap(byte[] pixelData, uint i)
        {
            byte temp = pixelData[i + 2];
            pixelData[i + 2] = pixelData[i + 0];
            pixelData[i + 0] = temp;
        }

        private static uint BytesPerPixelFromBitmapData(BitmapData bitmapData)
        {
            switch (bitmapData.PixelFormat)
            {
                case PixelFormat.Format32bppArgb:
                    return 4;

                case PixelFormat.Format32bppRgb:
                    return 3;

                default:
                    throw new InvalidDataException("Only 32 bit RGB(A) PNGs are supported by PNGDecrusher");
            }
        }

        private static bool BitmapDataHasAlpha(BitmapData bitmapData)
        {
            switch (bitmapData.PixelFormat)
            {
                case PixelFormat.Format32bppArgb:
                    return true;

                case PixelFormat.Format32bppRgb:
                    return false;

                default:
                    throw new InvalidDataException("Only 32 bit RGB(A) PNGs are supported by PNGDecrusher");
            }
        }

        private static void ReversePremultipliedAlpha(byte[] pixelData, uint startOffset)
        {
            // premultipliedValue = originalValue * (alpha / 255)
            //                    = (originalValue * alpha) / 255
            // therefore
            //
            // oldValue = (premultipliedValue * 255) / alpha

            byte alpha = pixelData[startOffset + 3];

            if (alpha == 0)
            {
                return;
            }

            pixelData[startOffset + 0] = (byte)((pixelData[startOffset + 0] * 255) / alpha);
            pixelData[startOffset + 1] = (byte)((pixelData[startOffset + 1] * 255) / alpha);
            pixelData[startOffset + 2] = (byte)((pixelData[startOffset + 2] * 255) / alpha);
        }
    }
}