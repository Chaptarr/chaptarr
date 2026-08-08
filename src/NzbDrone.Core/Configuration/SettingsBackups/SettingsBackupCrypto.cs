using System;
using System.Security.Cryptography;

namespace NzbDrone.Core.Configuration.SettingsBackups
{
    internal static class SettingsBackupCrypto
    {
        public const string EnvelopeFormat = "chaptarr.settings-backup.envelope";
        public const int EnvelopeVersion = 1;
        public const string EnvelopeCipher = "aes-256-gcm";
        public const int DefaultKdfIterations = 100_000;
        public const int MinKdfIterations = 10_000;
        public const int MaxKdfIterations = 5_000_000;

        public static SettingsBackupEnvelope Encrypt(byte[] plaintext, string passphrase)
        {
            if (plaintext == null)
            {
                throw new ArgumentNullException(nameof(plaintext));
            }

            if (string.IsNullOrWhiteSpace(passphrase))
            {
                throw new ArgumentException("Passphrase is required", nameof(passphrase));
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var key = DeriveKey(passphrase, salt, DefaultKdfIterations);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

            // TODO: It might be prudent to use a better crypto algo
            // But for now we might as well do it right, setting our tag size to 16 bytes
            // for the constructor, since that's defined above

            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            return new SettingsBackupEnvelope
            {
                Version = EnvelopeVersion,
                Cipher = EnvelopeCipher,
                SaltB64 = Convert.ToBase64String(salt),
                NonceB64 = Convert.ToBase64String(nonce),
                TagB64 = Convert.ToBase64String(tag),
                CiphertextB64 = Convert.ToBase64String(ciphertext)
            };
        }

        public static byte[] Decrypt(SettingsBackupEnvelope envelope, string passphrase)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (!string.Equals(envelope.Format, EnvelopeFormat, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Invalid backup envelope format");
            }

            if (envelope.Version != EnvelopeVersion)
            {
                throw new ArgumentException($"Unsupported backup envelope version: {envelope.Version}");
            }

            if (!string.Equals(envelope.Cipher, EnvelopeCipher, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Unsupported backup envelope cipher: {envelope.Cipher ?? "<null>"}");
            }

            var salt = Convert.FromBase64String(envelope.SaltB64);
            var nonce = Convert.FromBase64String(envelope.NonceB64);
            var tag = Convert.FromBase64String(envelope.TagB64);
            var ciphertext = Convert.FromBase64String(envelope.CiphertextB64);

            var iterations = envelope.KdfIterations > 0 ? envelope.KdfIterations : DefaultKdfIterations;
            if (iterations < MinKdfIterations || iterations > MaxKdfIterations)
            {
                throw new ArgumentException($"Invalid KDF iterations in backup envelope: {iterations}");
            }

            var key = DeriveKey(passphrase, salt, iterations);

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return plaintext;
        }

        private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) 
            =>Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
    }
}
