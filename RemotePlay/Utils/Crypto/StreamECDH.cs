using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using System.Security.Cryptography;
namespace RemotePlay.Utils.Crypto
{
    public class StreamECDH
    {
        public byte[] HandshakeKey { get; private set; }
        public byte[] PrivateKey { get; private set; }
        public byte[] PublicKey { get; private set; }
        public byte[] PublicSig { get; private set; }
        private ECPrivateKeyParameters _localEC;

        public StreamECDH(byte[]? handshake = null, byte[]? privateKey = null)
        {
            HandshakeKey = handshake ?? RandomBytes(16);
            _localEC = SetLocalEC(privateKey ?? RandomBytes(32));
            PrivateKey = _localEC.D.ToByteArrayUnsigned();
            PublicKey = SetPublicKey(_localEC);
            PublicSig = GetKeySig(HandshakeKey, PublicKey);
        }

        private ECPrivateKeyParameters SetLocalEC(byte[] key)
        {
            //key = LeftPadTo(key, 32); // 强制32字节
            var domain = SecNamedCurves.GetByName("secp256k1");
            var d = new BigInteger(1, key);
            return new ECPrivateKeyParameters(d, new ECDomainParameters(domain.Curve, domain.G, domain.N, domain.H));
        }

        private byte[] SetPublicKey(ECPrivateKeyParameters localEC)
        {
            var q = localEC.Parameters.G.Multiply(localEC.D).Normalize();
            return q.GetEncoded(false); // Uncompressed
        }

        private byte[] GetKeySig(byte[] handshakeKey, byte[] publicKey)
        {
            using var hmac = new HMACSHA256(handshakeKey);
            return hmac.ComputeHash(publicKey);
        }

        public byte[] GetSecret(byte[] remoteKey)
        {
            var domain = SecNamedCurves.GetByName("secp256k1");
            var q = domain.Curve.DecodePoint(remoteKey);
            var pubKey = new ECPublicKeyParameters(q, new ECDomainParameters(domain.Curve, domain.G, domain.N, domain.H));

            var agree = new Org.BouncyCastle.Crypto.Agreement.ECDHBasicAgreement();
            agree.Init(_localEC);
            var shared = agree.CalculateAgreement(pubKey);

            var secret = shared.ToByteArrayUnsigned();
            return LeftPadTo(secret, 32); // 保证32字节
        }

        private static byte[] LeftPadTo(byte[] src, int len)
        {
            if (src.Length == len) return src;
            if (src.Length > len) return src[^len..];
            var buf = new byte[len];
            Buffer.BlockCopy(src, 0, buf, len - src.Length, src.Length);
            return buf;
        }

        public bool VerifyRemoteSig(byte[] remoteKey, byte[] remoteSig)
        {
            using var hmac = new HMACSHA256(HandshakeKey);
            var calc = hmac.ComputeHash(remoteKey);
            return calc.SequenceEqual(remoteSig);
        }

        private static byte[] RandomBytes(int len)
        {
            var buf = new byte[len];
            RandomNumberGenerator.Fill(buf);
            return buf;
        }

        public bool SetSecret(byte[] remoteKey, byte[] remoteSig, out byte[] secret)
        {
            secret = Array.Empty<byte>();
            if (!VerifyRemoteSig(remoteKey, remoteSig))
                return false;
            secret = GetSecret(remoteKey);
            return true;
        }
    }
}
