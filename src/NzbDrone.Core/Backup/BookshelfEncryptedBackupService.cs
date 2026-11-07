using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Core.Backup
{
    public interface IBookshelfEncryptedBackupService
    {
        void Encrypt(string sourcePath, string destinationPath, string passphrase);
        void Decrypt(string sourcePath, string destinationPath, string passphrase);
    }

    public class BookshelfEncryptedBackupService : IBookshelfEncryptedBackupService
    {
        public const string PassphraseHeader = "X-Bookshelf-Backup-Passphrase";
        public const int MinimumPassphraseLength = 16;
        public const int MaximumPassphraseBytes = 4096;

        private const int Iterations = 600000;
        private const int KeyLength = 32;
        private const int SaltLength = 16;
        private const int NoncePrefixLength = 8;
        private const int NonceLength = 12;
        private const int TagLength = 16;
        private const int ChunkSize = 1024 * 1024;
        private const int HeaderLength = 8 + sizeof(uint) + SaltLength + NoncePrefixLength;
        private const long MaximumPlaintextBytes = 900_000_000;
        private const long MaximumChunkCount = (MaximumPlaintextBytes + ChunkSize - 1) / ChunkSize;
        private const long MaximumRecordOverhead = (MaximumChunkCount + 1) * (sizeof(uint) + TagLength);
        private const long MaximumEncryptedBytes = MaximumPlaintextBytes + MaximumRecordOverhead + HeaderLength;
        private const string InvalidBackupMessage = "The encrypted backup is invalid or the passphrase is incorrect.";

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BKSENC01");

        public static bool IsValidPassphrase(string passphrase)
        {
            return !string.IsNullOrWhiteSpace(passphrase) &&
                   passphrase.Length >= MinimumPassphraseLength &&
                   Encoding.UTF8.GetByteCount(passphrase) <= MaximumPassphraseBytes;
        }

        public void Encrypt(string sourcePath, string destinationPath, string passphrase)
        {
            ValidatePassphrase(passphrase);

            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);

            if (input.Length > MaximumPlaintextBytes)
            {
                throw new InvalidDataException("The backup is too large to encrypt.");
            }

            var header = new byte[HeaderLength];
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(Magic.Length, sizeof(uint)), ChunkSize);

            var saltOffset = Magic.Length + sizeof(uint);
            var salt = header.AsSpan(saltOffset, SaltLength);
            RandomNumberGenerator.Fill(salt);

            var noncePrefixOffset = saltOffset + SaltLength;
            var noncePrefix = header.AsSpan(noncePrefixOffset, NoncePrefixLength);
            RandomNumberGenerator.Fill(noncePrefix);

            var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
            var key = Rfc2898DeriveBytes.Pbkdf2(passphraseBytes, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);
            var destinationCreated = false;

            try
            {
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
                destinationCreated = true;
                using var aes = new AesGcm(key, TagLength);
                output.Write(header);

                var plaintext = new byte[ChunkSize];
                var ciphertext = new byte[ChunkSize];
                var tag = new byte[TagLength];
                var lengthBytes = new byte[sizeof(uint)];
                var chunkIndex = 0U;

                int read;
                while ((read = input.Read(plaintext, 0, plaintext.Length)) > 0)
                {
                    var associatedData = CreateAssociatedData(header, chunkIndex, (uint)read);
                    var nonce = CreateNonce(noncePrefix, chunkIndex);
                    aes.Encrypt(nonce, plaintext.AsSpan(0, read), ciphertext.AsSpan(0, read), tag, associatedData);

                    BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)read);
                    output.Write(lengthBytes);
                    output.Write(ciphertext, 0, read);
                    output.Write(tag);

                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, read));
                    chunkIndex++;
                }

                var finalNonce = CreateNonce(noncePrefix, chunkIndex);
                var finalAssociatedData = CreateAssociatedData(header, chunkIndex, 0);
                aes.Encrypt(finalNonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, finalAssociatedData);

                BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, 0);
                output.Write(lengthBytes);
                output.Write(tag);
                output.Flush(true);
            }
            catch
            {
                if (destinationCreated)
                {
                    TryDelete(destinationPath);
                }

                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);
                CryptographicOperations.ZeroMemory(key);
            }
        }

        public void Decrypt(string sourcePath, string destinationPath, string passphrase)
        {
            ValidatePassphrase(passphrase);

            var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
            byte[] key = null;
            var destinationCreated = false;

            try
            {
                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);

                if (input.Length < HeaderLength + sizeof(uint) + TagLength || input.Length > MaximumEncryptedBytes)
                {
                    throw new InvalidDataException(InvalidBackupMessage);
                }

                var header = new byte[HeaderLength];
                ReadExactly(input, header);

                if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) ||
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(Magic.Length, sizeof(uint))) != ChunkSize)
                {
                    throw new InvalidDataException(InvalidBackupMessage);
                }

                var saltOffset = Magic.Length + sizeof(uint);
                var salt = header.AsSpan(saltOffset, SaltLength);
                var noncePrefixOffset = saltOffset + SaltLength;
                var noncePrefix = header.AsSpan(noncePrefixOffset, NoncePrefixLength);
                key = Rfc2898DeriveBytes.Pbkdf2(passphraseBytes, salt, Iterations, HashAlgorithmName.SHA256, KeyLength);

                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
                destinationCreated = true;
                using var aes = new AesGcm(key, TagLength);

                var ciphertext = new byte[ChunkSize];
                var plaintext = new byte[ChunkSize];
                var tag = new byte[TagLength];
                var lengthBytes = new byte[sizeof(uint)];
                var chunkIndex = 0U;
                long totalBytes = 0;

                while (true)
                {
                    ReadExactly(input, lengthBytes);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);

                    if (length == 0)
                    {
                        ReadExactly(input, tag);
                        var finalNonce = CreateNonce(noncePrefix, chunkIndex);
                        var finalAssociatedData = CreateAssociatedData(header, chunkIndex, 0);
                        aes.Decrypt(finalNonce, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, finalAssociatedData);

                        if (input.ReadByte() != -1)
                        {
                            throw new InvalidDataException(InvalidBackupMessage);
                        }

                        break;
                    }

                    if (length > ChunkSize || chunkIndex == uint.MaxValue || totalBytes + length > MaximumPlaintextBytes)
                    {
                        throw new InvalidDataException(InvalidBackupMessage);
                    }

                    var chunkLength = (int)length;
                    ReadExactly(input, ciphertext.AsSpan(0, chunkLength));
                    ReadExactly(input, tag);

                    var associatedData = CreateAssociatedData(header, chunkIndex, length);
                    var nonce = CreateNonce(noncePrefix, chunkIndex);
                    aes.Decrypt(nonce, ciphertext.AsSpan(0, chunkLength), tag, plaintext.AsSpan(0, chunkLength), associatedData);

                    output.Write(plaintext, 0, chunkLength);
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, chunkLength));
                    totalBytes += chunkLength;
                    chunkIndex++;
                }

                output.Flush(true);
            }
            catch (CryptographicException exception)
            {
                if (destinationCreated)
                {
                    TryDelete(destinationPath);
                }

                throw new InvalidDataException(InvalidBackupMessage, exception);
            }
            catch
            {
                if (destinationCreated)
                {
                    TryDelete(destinationPath);
                }

                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passphraseBytes);

                if (key != null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }

        private static void ValidatePassphrase(string passphrase)
        {
            if (!IsValidPassphrase(passphrase))
            {
                throw new ArgumentException($"Passphrase must contain at least {MinimumPassphraseLength} characters and no more than {MaximumPassphraseBytes} UTF-8 bytes.", nameof(passphrase));
            }
        }

        private static byte[] CreateNonce(ReadOnlySpan<byte> noncePrefix, uint chunkIndex)
        {
            var nonce = new byte[NonceLength];
            noncePrefix.CopyTo(nonce);
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixLength), chunkIndex);
            return nonce;
        }

        private static byte[] CreateAssociatedData(byte[] header, uint chunkIndex, uint length)
        {
            var associatedData = new byte[HeaderLength + sizeof(uint) + sizeof(uint)];
            header.CopyTo(associatedData, 0);
            BinaryPrimitives.WriteUInt32BigEndian(associatedData.AsSpan(HeaderLength, sizeof(uint)), chunkIndex);
            BinaryPrimitives.WriteUInt32BigEndian(associatedData.AsSpan(HeaderLength + sizeof(uint), sizeof(uint)), length);
            return associatedData;
        }

        private static void ReadExactly(Stream stream, Span<byte> buffer)
        {
            var offset = 0;

            while (offset < buffer.Length)
            {
                var read = stream.Read(buffer[offset..]);

                if (read == 0)
                {
                    throw new InvalidDataException(InvalidBackupMessage);
                }

                offset += read;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Preserve the original encryption or decryption failure.
            }
        }
    }
}
