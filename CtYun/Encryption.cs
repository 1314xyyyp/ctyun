using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace CtYun
{
    /// <summary>
    /// 处理服务端 REDQ 保活质询：从质询包中取出 RSA 公钥，
    /// 按官方网页端一致的 OAEP-SHA1 填充加密后回包。
    /// 无会话状态：每次调用独立解析当前质询包。
    /// </summary>
    public class Encryption
    {
        // 质询包结构（逆向自官方网页端）：
        // 16 字节头 + 32 字节保留 + 129 字节 RSA 模数 N + 1 字节保留 + 3 字节指数 E
        private const int NOffset = 16 + 32;
        private const int NLength = 129;            // 1024-bit RSA
        private const int EOffset = 16 + 163;
        private const int ELength = 3;
        private const int MinPacketSize = EOffset + ELength;

        public uint AuthMechanism { get; set; } = 1;

        /// <exception cref="InvalidOperationException">质询包长度或内容异常（协议可能已变更）</exception>
        public byte[] Execute(byte[] challenge)
        {
            if (challenge == null || challenge.Length < MinPacketSize)
            {
                throw new InvalidOperationException(
                    $"REDQ 质询包长度异常：{challenge?.Length ?? 0} 字节（至少需要 {MinPacketSize}），协议可能已变更");
            }

            var (n, e) = GetPublicKey(challenge);
            if (e < 3 || e > 0xFFFFFF || n.IsZero)
            {
                throw new InvalidOperationException($"REDQ 质询包中的 RSA 公钥非法（e={e}），协议可能已变更");
            }

            byte[] encrypted = EncryptWithOaep(128, "", n, e);
            return ToBuffer(encrypted);
        }

        private static (BigInteger N, int E) GetPublicKey(byte[] challenge)
        {
            var n = new BigInteger(challenge.AsSpan(NOffset, NLength), isUnsigned: true, isBigEndian: true);

            var eSource = challenge.AsSpan(EOffset, ELength);
            int e = (eSource[0] << 16) | (eSource[1] << 8) | eSource[2];

            return (n, e);
        }

        private static byte[] EncryptWithOaep(int keyLen, string label, BigInteger n, int e)
        {
            byte[] seed = new byte[20];
            RandomNumberGenerator.Fill(seed);

            int hLen = 20; // SHA1
            int dbLen = keyLen - hLen - 1;
            byte[] db = new byte[dbLen];

            // DB: Hash(L) || PS || 01 || M（M 为空，与官方网页端一致）
            byte[] lHash = SHA1.HashData(Encoding.UTF8.GetBytes(label));
            lHash.CopyTo(db.AsSpan());
            db[db.Length - 1 - label.Length - 1] = 1;

            byte[] dbMask = MGF1(seed, dbLen);
            for (int k = 0; k < dbLen; k++) db[k] ^= dbMask[k];

            byte[] seedMask = MGF1(db, hLen);
            for (int k = 0; k < hLen; k++) seed[k] ^= seedMask[k];

            byte[] em = new byte[keyLen];
            seed.CopyTo(em.AsSpan(1, hLen));
            db.CopyTo(em.AsSpan(1 + hLen));

            var m = new BigInteger(em, isUnsigned: true, isBigEndian: true);
            var resultInt = BigInteger.ModPow(m, e, n);

            byte[] resultBytes = resultInt.ToByteArray(isUnsigned: true, isBigEndian: true);
            if (resultBytes.Length == keyLen) return resultBytes;

            byte[] final = new byte[keyLen];
            resultBytes.CopyTo(final.AsSpan(keyLen - resultBytes.Length));
            return final;
        }

        private static byte[] MGF1(ReadOnlySpan<byte> seed, int maskLen)
        {
            byte[] mask = new byte[maskLen];
            byte[] counter = new byte[4];
            int offset = 0;
            uint n = 0;

            while (offset < maskLen)
            {
                BinaryPrimitives.WriteUInt32BigEndian(counter, n);

                byte[] block = new byte[seed.Length + 4];
                seed.CopyTo(block);
                counter.CopyTo(block.AsSpan(seed.Length));

                byte[] hash = SHA1.HashData(block);
                int copyLen = Math.Min(hash.Length, maskLen - offset);
                hash.AsSpan(0, copyLen).CopyTo(mask.AsSpan(offset));

                offset += hash.Length;
                n++;
            }
            return mask;
        }

        private byte[] ToBuffer(byte[] buffer)
        {
            byte[] result = new byte[4 + buffer.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(result, AuthMechanism);
            buffer.CopyTo(result.AsSpan(4));
            return result;
        }
    }
}
