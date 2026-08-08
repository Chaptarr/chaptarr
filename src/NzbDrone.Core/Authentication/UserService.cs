using System;
using System.Security.Cryptography;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Core.Authentication
{
    public interface IUserService
    {
        User Add(string username, string password);
        User Update(User user);
        User Upsert(string username, string password);
        User FindUser();
        User FindUser(string username, string password);
        User FindUser(Guid identifier);
    }

    public class UserService : IUserService
    {
        private const int Iterations = 10000;
        private const int SaltSize = 128 / 8;
        private const int HashSize = 256 / 8;

        private readonly IUserRepository _repo;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IDiskProvider _diskProvider;

        public UserService(IUserRepository repo, IAppFolderInfo appFolderInfo, IDiskProvider diskProvider)
        {
            _repo = repo;
            _appFolderInfo = appFolderInfo;
            _diskProvider = diskProvider;
        }

        public User Add(string username, string password)
        {
            var user = new User
            {
                Identifier = Guid.NewGuid(),
                Username = username.ToLowerInvariant()
            };

            SetUserHashedPassword(user, password);

            return _repo.Insert(user);
        }

        public User Update(User user)
        {
            return _repo.Update(user);
        }

        public User Upsert(string username, string password)
        {
            var user = FindUser();

            if (user == null)
            {
                return Add(username, password);
            }

            if (password.IsNotNullOrWhiteSpace())
            {
                SetUserHashedPassword(user, password);
            }

            user.Username = username.ToLowerInvariant();

            return Update(user);
        }

        public User FindUser()
        {
            return _repo.SingleOrDefault();
        }

        public User FindUser(string username, string password)
        {
            if (username.IsNullOrWhiteSpace() || password.IsNullOrWhiteSpace())
            {
                return null;
            }

            var user = _repo.FindUser(username.ToLowerInvariant());

            if (user == null)
            {
                return null;
            }

            if (user.Salt.IsNullOrWhiteSpace())
            {
                // If password matches stored SHA256 hash, update to salted hash and verify.
                if (user.Password == password.SHA256Hash())
                {
                    SetUserHashedPassword(user, password);
                    return Update(user);
                }

                return null;
            }

            if (VerifyHashedPassword(user, password))
            {
                return user;
            }

            return null;
        }

        public User FindUser(Guid identifier)
        {
            return _repo.FindUser(identifier);
        }

        private User SetUserHashedPassword(User user, string password)
        {
            var salt = GenerateSalt();

            user.Iterations = Iterations;
            user.Salt = Convert.ToBase64String(salt);
            user.Password = GetHashedPassword(password, salt, Iterations);

            return user;
        }

        private static byte[] GenerateSalt()
        {
            var salt = new byte[SaltSize];
            RandomNumberGenerator.Fill(salt);

            return salt;
        }

        private static string GetHashedPassword(string password, byte[] salt, int iterations)
        {
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, HashSize);
            return Convert.ToBase64String(hash);
        }

        private static bool VerifyHashedPassword(User user, string password)
        {
            if (user.Iterations <= 0 || user.Salt.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(user.Salt);
                var expected = Convert.FromBase64String(user.Password);
                var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, user.Iterations, HashAlgorithmName.SHA512, expected.Length);

                return CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
