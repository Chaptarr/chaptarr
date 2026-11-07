using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Core.Backup
{
    /// <summary>
    /// Chunked AES-GCM envelope for full backup archives. Each record and the
    /// immutable header are authenticated, including an explicit final record
    /// so truncation is rejected.
    /// </summary>
    internal static class BackupArchiveEncryption
    {
        public const string PassphraseFileEnvironmentVariable = "CHAPTARR_BACKUP_ENCRYPTION_KEY_FILE";

        private const int HeaderSize = 40;
        private const int SaltSize = 16;
        private const int NoncePrefixSize = 8;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private const int ChunkSize = 1024 * 1024;
        private const int KdfIterations = 600_000;
        private const int MinKdfIterations = 100_000;
        private const int MaxKdfIterations = 2_000_000;
        private const int MinPassphraseLength = 16;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CHAPTBK1");

        public static string GetConfiguredPassphrase()
        {
            var passphraseFile = Environment.GetEnvironmentVariable(PassphraseFileEnvironmentVariable);

            if (string.IsNullOrEmpty(passphraseFile))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(passphraseFile))
            {
                throw new InvalidOperationException($"{PassphraseFileEnvironmentVariable} must contain a valid file path.");
            }

            var file = new FileInfo(passphraseFile);
            if (!file.Exists || file.Length > 4096)
            {
                throw new InvalidOperationException($"The file configured by {PassphraseFileEnvironmentVariable} must exist and be no larger than 4096 bytes.");
            }

            var passphrase = File.ReadAllText(passphraseFile, Encoding.UTF8).TrimEnd('\r', '\n');
            if (passphrase.Count(character => !char.IsWhiteSpace(character)) < MinPassphraseLength)
            {
                throw new InvalidOperationException($"The secret file configured by {PassphraseFileEnvironmentVariable} must contain at least {MinPassphraseLength} non-whitespace characters.");
            }

            return passphrase;
        }

        public static void Encrypt(string sourcePath, string destinationPath, string passphrase)
        {
            var header = CreateHeader(passphrase, out var key);
            var nonce = new byte[NonceSize];
            var aad = new byte[HeaderSize + 8];
            var plaintext = new byte[ChunkSize];
            var ciphertext = new byte[ChunkSize];
            var tag = new byte[TagSize];

            try
            {
                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, FileOptions.SequentialScan);
                output.Write(header);

                using var aes = new AesGcm(key, TagSize);
                uint counter = 0;
                while (true)
                {
                    var length = ReadChunk(input, plaintext);
                    var lengthBytes = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, (uint)length);
                    var recordAad = CreateRecordAad(header, counter, lengthBytes, aad);
                    CreateNonce(header, counter, nonce);

                    if (length == 0)
                    {
                        aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag, recordAad);
                        output.Write(lengthBytes);
                        output.Write(tag);
                        output.Flush(true);
                        break;
                    }

                    aes.Encrypt(nonce, plaintext.AsSpan(0, length), ciphertext.AsSpan(0, length), tag, recordAad);
                    output.Write(lengthBytes);
                    output.Write(ciphertext, 0, length);
                    output.Write(tag);
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, length));
                    CryptographicOperations.ZeroMemory(ciphertext.AsSpan(0, length));

                    if (counter == uint.MaxValue)
                    {
                        throw new InvalidDataException("Encrypted backup contains too many records.");
                    }

                    counter++;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(aad);
            }
        }

        public static void Decrypt(string sourcePath, string destinationPath, string passphrase)
        {
            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            byte[] header = null;
            byte[] key = null;
            byte[] plaintext = null;
            byte[] ciphertext = null;
            byte[] aad = null;

            try
            {
                using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, FileOptions.SequentialScan);
                header = new byte[HeaderSize];
                ReadExactly(input, header);

                if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
                {
                    throw new InvalidDataException("Unsupported encrypted backup format.");
                }

                var iterations = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                if (iterations < MinKdfIterations || iterations > MaxKdfIterations || chunkSize < 4096 || chunkSize > ChunkSize)
                {
                    throw new InvalidDataException("Encrypted backup parameters are outside supported limits.");
                }

                key = Rfc2898DeriveBytes.Pbkdf2(
                    passphrase,
                    header.AsSpan(16, SaltSize),
                    (int)iterations,
                    HashAlgorithmName.SHA256,
                    32);
                plaintext = new byte[chunkSize];
                ciphertext = new byte[chunkSize];
                aad = new byte[HeaderSize + 8];

                using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, (int)chunkSize, FileOptions.SequentialScan);
                using var aes = new AesGcm(key, TagSize);
                uint counter = 0;

                while (true)
                {
                    var lengthBytes = new byte[4];
                    ReadExactly(input, lengthBytes);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
                    if (length > chunkSize)
                    {
                        throw new InvalidDataException("Encrypted backup record exceeds the allowed chunk size.");
                    }

                    if (length > 0)
                    {
                        ReadExactly(input, ciphertext.AsSpan(0, (int)length));
                    }

                    ReadExactly(input, tag);
                    var recordAad = CreateRecordAad(header, counter, lengthBytes, aad);
                    CreateNonce(header, counter, nonce);

                    if (length == 0)
                    {
                        aes.Decrypt(nonce, ReadOnlySpan<byte>.Empty, tag, Span<byte>.Empty, recordAad);
                        if (input.ReadByte() != -1)
                        {
                            throw new InvalidDataException("Encrypted backup has trailing data.");
                        }

                        output.Flush(true);
                        break;
                    }

                    aes.Decrypt(nonce, ciphertext.AsSpan(0, (int)length), tag, plaintext.AsSpan(0, (int)length), recordAad);
                    output.Write(plaintext, 0, (int)length);
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, (int)length));
                    CryptographicOperations.ZeroMemory(ciphertext.AsSpan(0, (int)length));

                    if (counter == uint.MaxValue)
                    {
                        throw new InvalidDataException("Encrypted backup contains too many records.");
                    }

                    counter++;
                }
            }
            finally
            {
                if (key != null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }

                if (plaintext != null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }

                if (ciphertext != null)
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                }

                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(aad ?? Array.Empty<byte>());
            }
        }

        private static byte[] CreateHeader(string passphrase, out byte[] key)
        {
            var header = new byte[HeaderSize];
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var noncePrefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
            Magic.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), KdfIterations);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12, 4), ChunkSize);
            salt.CopyTo(header, 16);
            noncePrefix.CopyTo(header, 32);
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(noncePrefix);
            return header;
        }

        private static byte[] CreateRecordAad(byte[] header, uint counter, byte[] lengthBytes, byte[] buffer)
        {
            header.CopyTo(buffer, 0);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(HeaderSize, 4), counter);
            lengthBytes.CopyTo(buffer, HeaderSize + 4);
            return buffer;
        }

        private static void CreateNonce(byte[] header, uint counter, byte[] nonce)
        {
            header.AsSpan(32, NoncePrefixSize).CopyTo(nonce);
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize, 4), counter);
        }

        private static int ReadChunk(Stream input, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = input.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total;
        }

        private static void ReadExactly(Stream input, byte[] buffer)
        {
            ReadExactly(input, buffer.AsSpan());
        }

        private static void ReadExactly(Stream input, Span<byte> buffer)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = input.Read(buffer.Slice(offset));
                if (read == 0)
                {
                    throw new EndOfStreamException("Encrypted backup is incomplete.");
                }

                offset += read;
            }
        }
    }
}
