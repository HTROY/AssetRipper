using AssetRipper.IO.Files.Exceptions;
using AssetRipper.IO.Files.Streams;
using AssetRipper.IO.Files.Streams.Smart;
using K4os.Compression.LZ4;
using System.Buffers;

namespace AssetRipper.IO.Files.BundleFiles.FileStream;

internal sealed class BundleFileBlockReader : IDisposable
{
	public BundleFileBlockReader(SmartStream stream, BlocksInfo blocksInfo)
	{
		m_stream = stream;
		m_blocksInfo = blocksInfo;
		m_dataOffset = stream.Position;
	}

	~BundleFileBlockReader()
	{
		Dispose(false);
	}

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	public SmartStream ReadEntry(FileStreamNode entry)
	{
		ObjectDisposedException.ThrowIf(m_isDisposed, typeof(BundleFileBlockReader));

		// Avoid storing entire non-compresed entries in memory by mapping a stream to the block location.
		if (m_blocksInfo.StorageBlocks.Length == 1 && m_blocksInfo.StorageBlocks[0].CompressionType == CompressionType.None)
		{
			if (m_dataOffset + entry.Offset + entry.Size > m_stream.Length)
			{
				throw new InvalidFormatException("Entry extends beyond the end of the stream.");
			}
			return m_stream.CreatePartial(m_dataOffset + entry.Offset, entry.Size);
		}

		// find block offsets
		int blockIndex;
		long blockCompressedOffset = 0;
		long blockDecompressedOffset = 0;
		for (blockIndex = 0; blockDecompressedOffset + m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize <= entry.Offset; blockIndex++)
		{
			blockCompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].CompressedSize;
			blockDecompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize;
		}
		long entryOffsetInsideBlock = entry.Offset - blockDecompressedOffset;

		using SmartStream entryStream = CreateStream(entry.Size);
		long left = entry.Size;
		m_stream.Position = m_dataOffset + blockCompressedOffset;

		// copy data of all blocks used by current entry to new stream
		while (left > 0)
		{
			long blockStreamOffset;
			Stream blockStream;
			StorageBlock block = m_blocksInfo.StorageBlocks[blockIndex];
			if (m_cachedBlockIndex == blockIndex)
			{
				// data of the previous entry is in the same block as this one
				// so we don't need to unpack it once again. Instead we can use cached stream
				blockStreamOffset = 0;
				blockStream = m_cachedBlockStream;
				m_stream.Position += block.CompressedSize;
			}
			else
			{
				CompressionType compressType = block.CompressionType;
				if (compressType is CompressionType.None)
				{
					blockStreamOffset = m_dataOffset + blockCompressedOffset;
					blockStream = m_stream;
				}
				else
				{
					blockStreamOffset = 0;
					if (m_cachedBlockArray != null)
					{
						ArrayPool<byte>.Shared.Return(m_cachedBlockArray);
						m_cachedBlockArray = null;
					}
					m_cachedBlockIndex = blockIndex;
					m_cachedBlockStream.Move(CreateTemporaryStream(block.UncompressedSize, out m_cachedBlockArray));
					switch (compressType)
					{
						case CompressionType.Lzma:
							LzmaCompression.DecompressLzmaStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
							break;

						case CompressionType.Lz4:
						case CompressionType.Lz4HC:
							uint uncompressedSize = block.UncompressedSize;
							byte[] uncompressedBytes = new byte[uncompressedSize];
							byte[] compressedBytes = new BinaryReader(m_stream).ReadBytes((int)block.CompressedSize);
							int bytesWritten = LZ4Codec.Decode(compressedBytes, uncompressedBytes);
							if (bytesWritten < 0)
							{
								DecompressionFailedException.ThrowNoBytesWritten(entry.PathFixed, compressType);
							}
							else if (bytesWritten != uncompressedSize)
							{
								DecompressionFailedException.ThrowIncorrectNumberBytesWritten(entry.PathFixed, compressType, uncompressedSize, bytesWritten);
							}
							new MemoryStream(uncompressedBytes).CopyTo(m_cachedBlockStream);
							break;

						case CompressionType.Lzham:
							UnsupportedBundleDecompression.ThrowLzham(entry.PathFixed);
							break;

						default:
							if (ZstdCompression.IsZstd(m_stream))
							{
								ZstdCompression.DecompressStream(m_stream, block.CompressedSize, m_cachedBlockStream, block.UncompressedSize);
							}
							else
							{
								UnsupportedBundleDecompression.Throw(entry.PathFixed, compressType);
							}
							break;
					}
					blockStream = m_cachedBlockStream;
				}
			}

			// consider next offsets:
			// 1) block - if it is new stream then offset is 0, otherwise offset of this block in the bundle file
			// 2) entry - if this is first block for current entry then it is offset of this entry related to this block
			//			  otherwise 0
			long blockSize = block.UncompressedSize - entryOffsetInsideBlock;
			blockStream.Position = blockStreamOffset + entryOffsetInsideBlock;
			entryOffsetInsideBlock = 0;

			long size = Math.Min(blockSize, left);
			if (blockStream.Position + size > blockStream.Length)
			{
				throw new InvalidFormatException("Block extends beyond the end of the stream.");
			}
			using PartialStream partialStream = new(blockStream, blockStream.Position, size);
			partialStream.CopyTo(entryStream);
			blockIndex++;

			blockCompressedOffset += block.CompressedSize;
			left -= size;

		}
		if (left < 0)
		{
			DecompressionFailedException.ThrowReadMoreThanExpected(entry.PathFixed, entry.Size, entry.Size - left);
		}
		entryStream.Position = 0;
		return entryStream.CreateReference();
	}

	/// <summary>
	/// Reads a byte range of an entry without materializing the whole entry.
	/// </summary>
	/// <remarks>
	/// This is used by metadata-only loading, which only needs the header, the metadata and a
	/// few small objects. Unlike <see cref="ReadEntry"/>, this method decompresses only the
	/// storage blocks that overlap the requested range.
	/// </remarks>
	/// <param name="entry">The entry to read from.</param>
	/// <param name="offset">The offset of the range relative to the start of the entry.</param>
	/// <param name="size">The number of bytes to read.</param>
	/// <returns>The requested bytes.</returns>
	public byte[] ReadEntryRange(FileStreamNode entry, long offset, int size)
	{
		ObjectDisposedException.ThrowIf(m_isDisposed, typeof(BundleFileBlockReader));
		ArgumentOutOfRangeException.ThrowIfNegative(offset);
		ArgumentOutOfRangeException.ThrowIfNegative(size);
		if (offset + size > entry.Size)
		{
			throw new InvalidFormatException("Entry range extends beyond the end of the entry.");
		}
		if (size == 0)
		{
			return [];
		}

		byte[] result = new byte[size];
		int blockIndex = 0;
		long blockCompressedOffset = 0;
		long blockDecompressedOffset = 0;
		while (blockDecompressedOffset + m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize <= offset)
		{
			blockCompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].CompressedSize;
			blockDecompressedOffset += m_blocksInfo.StorageBlocks[blockIndex].UncompressedSize;
			blockIndex++;
		}

		int written = 0;
		long position = offset;
		while (written < size)
		{
			StorageBlock block = m_blocksInfo.StorageBlocks[blockIndex];
			long offsetInsideBlock = position - blockDecompressedOffset;
			int take = (int)Math.Min(block.UncompressedSize - offsetInsideBlock, (long)(size - written));
			ReadBlockRange(entry, block, blockCompressedOffset, offsetInsideBlock, result.AsSpan(written, take));
			written += take;
			position += take;
			blockCompressedOffset += block.CompressedSize;
			blockDecompressedOffset += block.UncompressedSize;
			blockIndex++;
		}
		return result;
	}

	/// <summary>
	/// Decompresses a single storage block (if necessary) and copies part of it to
	/// <paramref name="destination"/>.
	/// </summary>
	private void ReadBlockRange(FileStreamNode entry, StorageBlock block, long blockCompressedOffset, long offsetInsideBlock, Span<byte> destination)
	{
		CompressionType compressionType = block.CompressionType;
		if (compressionType is CompressionType.None)
		{
			m_stream.Position = m_dataOffset + blockCompressedOffset + offsetInsideBlock;
			m_stream.ReadExactly(destination);
			return;
		}

		byte[] uncompressedBytes = new byte[(int)block.UncompressedSize];
		m_stream.Position = m_dataOffset + blockCompressedOffset;
		switch (compressionType)
		{
			case CompressionType.Lzma:
				{
					using MemoryStream uncompressedStream = new(uncompressedBytes);
					LzmaCompression.DecompressLzmaStream(m_stream, block.CompressedSize, uncompressedStream, block.UncompressedSize);
				}
				break;

			case CompressionType.Lz4:
			case CompressionType.Lz4HC:
				{
					byte[] compressedBytes = new byte[(int)block.CompressedSize];
					m_stream.ReadExactly(compressedBytes);
					int bytesWritten = LZ4Codec.Decode(compressedBytes, uncompressedBytes);
					if (bytesWritten < 0)
					{
						DecompressionFailedException.ThrowNoBytesWritten(entry.PathFixed, compressionType);
					}
					else if (bytesWritten != (int)block.UncompressedSize)
					{
						DecompressionFailedException.ThrowIncorrectNumberBytesWritten(entry.PathFixed, compressionType, block.UncompressedSize, bytesWritten);
					}
				}
				break;

			case CompressionType.Lzham:
				UnsupportedBundleDecompression.ThrowLzham(entry.PathFixed);
				break;

			default:
				if (ZstdCompression.IsZstd(m_stream))
				{
					using MemoryStream uncompressedStream = new(uncompressedBytes);
					ZstdCompression.DecompressStream(m_stream, block.CompressedSize, uncompressedStream, block.UncompressedSize);
				}
				else
				{
					UnsupportedBundleDecompression.Throw(entry.PathFixed, compressionType);
				}
				break;
		}
		uncompressedBytes.AsSpan((int)offsetInsideBlock, destination.Length).CopyTo(destination);
	}

	private void Dispose(bool disposing)
	{
		m_isDisposed = true;
		m_cachedBlockStream.FreeReference();
		if (m_cachedBlockArray != null)
		{
			ArrayPool<byte>.Shared.Return(m_cachedBlockArray);
			m_cachedBlockArray = null;
		}
	}

	private static SmartStream CreateStream(long decompressedSize)
	{
		return decompressedSize switch
		{
			> MaxMemoryStreamLength => SmartStream.CreateTemp(),
			> MaxPreAllocatedMemoryStreamLength => SmartStream.CreateMemory(),
			_ => SmartStream.CreateMemory(new byte[decompressedSize]),
		};
	}

	private static SmartStream CreateTemporaryStream(long decompressedSize, out byte[]? rentedArray)
	{
		if (decompressedSize > MaxMemoryStreamLength)
		{
			rentedArray = null;
			return SmartStream.CreateTemp();
		}
		else
		{
			rentedArray = ArrayPool<byte>.Shared.Rent((int)decompressedSize);
			return SmartStream.CreateMemory(rentedArray, 0, (int)decompressedSize);
		}
	}

	/// <summary>
	/// The arbitrary maximum size of a decompressed stream to be stored in RAM. 50 MB
	/// </summary>
	/// <remarks>
	/// This number can be set to any integer value, including <see cref="int.MaxValue"/>.
	/// Previously, this was actually set to <see cref="int.MaxValue"/>, but that can cause
	/// <see href="https://github.com/AssetRipper/AssetRipper/issues/1953">highly compressed games to use too much RAM</see>.
	/// </remarks>
	private const int MaxMemoryStreamLength = 50 * 1024 * 1024;
	/// <summary>
	/// The arbitrary maximum size of a decompressed stream to be pre-allocated. 30 MB
	/// </summary>
	/// <remarks>
	/// This number can be set to any integer value less than <see cref="MaxMemoryStreamLength"/>.
	/// </remarks>
	private const int MaxPreAllocatedMemoryStreamLength = 30 * 1024 * 1024;
	private readonly SmartStream m_stream;
	private readonly BlocksInfo m_blocksInfo = new();
	private readonly long m_dataOffset;

	private readonly SmartStream m_cachedBlockStream = SmartStream.CreateNull();
	private byte[]? m_cachedBlockArray;
	private int m_cachedBlockIndex = -1;

	private bool m_isDisposed = false;
}
