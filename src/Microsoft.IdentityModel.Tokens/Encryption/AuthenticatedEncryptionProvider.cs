﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Logging;
using System.Text;

namespace Microsoft.IdentityModel.Tokens
{
    public class AuthenticatedEncryptionProvider
    {
        private SymmetricSecurityKey _key;
        private string _algorithm;

        public AuthenticatedEncryptionProvider(SecurityKey key, string algorithm)
        {
            if (key == null)
                throw LogHelper.LogArgumentNullException(nameof(key));

            if (algorithm == null)
                throw LogHelper.LogArgumentNullException("algorithm");

            if (algorithm.Length == 0)
                throw LogHelper.LogException<ArgumentException>("Cannot encrypt empty 'algorithm'");

            _key = key as SymmetricSecurityKey;
            if (_key == null)
                throw LogHelper.LogArgumentException<ArgumentException>("key", "not symmetric key.");

            ValidateKeySize(_key.Key, algorithm);
            _algorithm = algorithm;
        }

        public virtual EncryptionResult Encrypt(byte[] plaintext, byte[] authenticatedData)
        {
            if (plaintext == null)
                throw LogHelper.LogArgumentNullException("plaintext");

            if (plaintext.Length == 0)
                throw LogHelper.LogException<ArgumentException>("Cannot encrypt empty 'plaintext'");

            if (authenticatedData == null)
                throw LogHelper.LogArgumentNullException("authenticatedData");

            byte[] aesKey;
            byte[] hmacKey;
            GetAlgorithmParameters(_algorithm, _key.Key, out aesKey, out hmacKey);

            Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            SignatureProvider symmetricSignatureProvider = _key.CryptoProviderFactory.CreateForSigning(new SymmetricSecurityKey(hmacKey), GetHashAlgorithm(_algorithm));

            var result = new EncryptionResult();
            result.CipherText = EncryptionUtilities.Transform(aes.CreateEncryptor(), plaintext, 0, plaintext.Length);
            result.Key = _key.Key;
            result.InitialVector = aes.IV;

            byte[] al = ConvertToBigEndian(authenticatedData.Length * 8);
            byte[] macBytes = new byte[authenticatedData.Length + result.InitialVector.Length + result.CipherText.Length + al.Length];
            Array.Copy(authenticatedData, 0, macBytes, 0, authenticatedData.Length);
            Array.Copy(result.InitialVector, 0, macBytes, authenticatedData.Length, result.InitialVector.Length);
            Array.Copy(result.CipherText, 0, macBytes, authenticatedData.Length + result.InitialVector.Length, result.CipherText.Length);
            Array.Copy(al, 0, macBytes, authenticatedData.Length + result.InitialVector.Length + result.CipherText.Length, al.Length);
            byte[] macHash = symmetricSignatureProvider.Sign(macBytes);
            result.AuthenticationTag = new byte[hmacKey.Length];
            Array.Copy(macHash, result.AuthenticationTag, result.AuthenticationTag.Length);

            return result;
        }

        public virtual byte[] Decrypt(byte[] ciphertext, byte[] authenticatedData, byte[] iv, byte[] authenticationTag)
        {
            if (ciphertext == null)
                throw LogHelper.LogArgumentNullException("ciphertext");

            if (ciphertext.Length == 0)
                throw LogHelper.LogException<ArgumentException>("Cannot encrypt empty 'ciphertext'");

            if (authenticatedData == null)
                // TODO (Yan) : Add exception log message and throw;
                throw LogHelper.LogArgumentNullException("authenticatedData");

            if (iv == null)
                throw LogHelper.LogArgumentNullException("iv");

            if (authenticationTag == null)
                throw LogHelper.LogArgumentNullException("authenticationTag");

            byte[] aesKey;
            byte[] hmacKey;
            GetAlgorithmParameters(_algorithm, _key.Key, out aesKey, out hmacKey);
            SymmetricSignatureProvider symmetricSignatureProvider = _key.CryptoProviderFactory.CreateForVerifying(new SymmetricSecurityKey(hmacKey), GetHashAlgorithm(_algorithm)) as SymmetricSignatureProvider;
            if (symmetricSignatureProvider == null)
                throw LogHelper.LogException<InvalidCastException>("Cannot get SymmetricSignatureProvider.");

            // Verify authentication Tag
            byte[] al = ConvertToBigEndian(authenticatedData.Length * 8);
            byte[] macBytes = new byte[authenticatedData.Length + iv.Length + ciphertext.Length + al.Length];
            Array.Copy(authenticatedData, 0, macBytes, 0, authenticatedData.Length);
            Array.Copy(iv, 0, macBytes, authenticatedData.Length, iv.Length);
            Array.Copy(ciphertext, 0, macBytes, authenticatedData.Length + iv.Length, ciphertext.Length);
            Array.Copy(al, 0, macBytes, authenticatedData.Length + iv.Length + ciphertext.Length, al.Length);
            if (!symmetricSignatureProvider.Verify(macBytes, authenticationTag, hmacKey.Length))
                throw LogHelper.LogException<ArgumentException>(string.Format("Failed to verify ciphertext with aad: '{0}'; iv: '{1}'; and authenticationTag: '{2}'.", Base64UrlEncoder.Encode(authenticatedData), Base64UrlEncoder.Encode(iv), Base64UrlEncoder.Encode(authenticationTag)));

            Aes aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = aesKey;
            aes.IV = iv;
            return EncryptionUtilities.Transform(aes.CreateDecryptor(), ciphertext, 0, ciphertext.Length);
        }

        private void GetAlgorithmParameters(string algorithm, byte[] key, out byte[] aes_key, out byte[] hmac_key)
        {
            switch (algorithm)
            {
                case SecurityAlgorithms.Aes128CbcHmacSha256:
                    {
                        if ((key.Length << 3) < 256)
                            // TODO (Yan) : Add log message
                            throw LogHelper.LogArgumentException<ArgumentOutOfRangeException>("key", LogMessages.IDX10628, 256);

                        hmac_key = new byte[128 >> 3];
                        aes_key = new byte[128 >> 3];
                        Array.Copy(key, hmac_key, 128 >> 3);
                        Array.Copy(key, 128 >> 3, aes_key, 0, 128 >> 3);
                        break;
                    }

                case SecurityAlgorithms.Aes256CbcHmacSha512:
                    {
                        if ((key.Length << 3) < 512)
                            throw LogHelper.LogArgumentException<ArgumentOutOfRangeException>("key", LogMessages.IDX10628, 512);

                        hmac_key = new byte[256 >> 3];
                        aes_key = new byte[256 >> 3];
                        Array.Copy(key, hmac_key, 256 >> 3);
                        Array.Copy(key, 256 >> 3, aes_key, 0, 256 >> 3);
                        break;
                    }

                default:
                    {
                        throw LogHelper.LogArgumentException<ArgumentOutOfRangeException>("algorithm", nameof(algorithm));
                    }
            }
        }

        private int GetKeySize(string algorithm)
        {
            switch (algorithm)
            {
                case SecurityAlgorithms.Aes128CbcHmacSha256:
                    return 256;

                case SecurityAlgorithms.Aes256CbcHmacSha512:
                    return 512;

                default:
                    //TODO (Yan) : Add new exception to logMessages and throw;
                    throw LogHelper.LogArgumentException<ArgumentException>(nameof(algorithm), String.Format("Unsupported algorithm: {0}", algorithm));
            }
        }

        private string GetHashAlgorithm(string algorithm)
        {
            switch (algorithm)
            {
                case SecurityAlgorithms.Aes128CbcHmacSha256:
                    return SecurityAlgorithms.HmacSha256;

                case SecurityAlgorithms.Aes256CbcHmacSha512:
                    return SecurityAlgorithms.HmacSha512;

                default:
                    //TODO (Yan) : Add new exception to logMessages and throw;
                    throw LogHelper.LogArgumentException<ArgumentException>(nameof(algorithm), String.Format("Unsupported algorithm: {0}", algorithm));
            }
        }

        private void ValidateKeySize(byte[] key, string algorithm)
        {
            switch (algorithm)
            {
                case SecurityAlgorithms.Aes128CbcHmacSha256:
                    {
                        if (key.Length != 32)
                            // TODO (Yan) : Add new exception to LogMessages and throw;
                            throw LogHelper.LogArgumentException<ArgumentOutOfRangeException>("key.KeySize", LogMessages.IDX10630, key, algorithm, key.Length << 3);
                        break;
                    }

                case SecurityAlgorithms.Aes256CbcHmacSha512:
                    {
                        if (key.Length != 64)
                            // TODO (Yan) : Add new exception to LogMessages and throw;
                            throw LogHelper.LogArgumentException<ArgumentOutOfRangeException>("key.KeySize", LogMessages.IDX10630, key, algorithm, key.Length << 3);
                        break;
                    }

                default:
                    //TODO (Yan) : Add new exception to logMessages and throw;
                    throw LogHelper.LogArgumentException<ArgumentException>(nameof(algorithm), String.Format("Unsupported algorithm: {0}", algorithm));
            }
        }

        private static byte[] ConvertToBigEndian(Int64 i)
        {
            byte[] temp = BitConverter.GetBytes(i);

            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(temp);
            }

            return temp;
        }
    }
}