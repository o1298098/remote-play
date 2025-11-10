namespace RemotePlay.Utils
{
    internal static class H264NalUtils
    {
        private static readonly byte[] StartCode3 = new byte[] { 0x00, 0x00, 0x01 };
        private static readonly byte[] StartCode4 = new byte[] { 0x00, 0x00, 0x00, 0x01 };

        public static bool TryExtractSpsPps(byte[] annexBFrame, out byte[] spsWithStartCode, out byte[] ppsWithStartCode)
        {
            spsWithStartCode = Array.Empty<byte>();
            ppsWithStartCode = Array.Empty<byte>();
            if (annexBFrame == null || annexBFrame.Length < 6) return false;

            var nalUnits = SplitAnnexB(annexBFrame);
            foreach (var unit in nalUnits)
            {
                if (unit.Payload.Length == 0) continue;
                int nalType = unit.Payload[0] & 0x1F;
                if (nalType == 7 && spsWithStartCode.Length == 0)
                {
                    spsWithStartCode = Combine(unit.StartCode, unit.Payload);
                }
                else if (nalType == 8 && ppsWithStartCode.Length == 0)
                {
                    ppsWithStartCode = Combine(unit.StartCode, unit.Payload);
                }
                if (spsWithStartCode.Length > 0 && ppsWithStartCode.Length > 0) return true;
            }
            return false;
        }

        private static byte[] Combine(byte[] a, byte[] b)
        {
            var buf = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, buf, 0, a.Length);
            Buffer.BlockCopy(b, 0, buf, a.Length, b.Length);
            return buf;
        }

        private static List<(byte[] StartCode, byte[] Payload)> SplitAnnexB(byte[] data)
        {
            var list = new List<(byte[] StartCode, byte[] Payload)>();
            int i = 0; int n = data.Length;
            while (i < n - 3)
            {
                int scLen = 0;
                if (MatchAt(data, i, StartCode4)) scLen = 4;
                else if (MatchAt(data, i, StartCode3)) scLen = 3;
                if (scLen == 0) { i++; continue; }
                int start = i + scLen;
                int j = start;
                while (j < n - 3)
                {
                    if (MatchAt(data, j, StartCode4) || MatchAt(data, j, StartCode3)) break;
                    j++;
                }
                var payload = new byte[j - start];
                Buffer.BlockCopy(data, start, payload, 0, payload.Length);
                var sc = scLen == 4 ? StartCode4 : StartCode3;
                list.Add((sc, payload));
                i = j;
            }
            return list;
        }

        private static bool MatchAt(byte[] data, int index, byte[] pat)
        {
            if (index + pat.Length > data.Length) return false;
            for (int k = 0; k < pat.Length; k++) if (data[index + k] != pat[k]) return false;
            return true;
        }
    }
}


