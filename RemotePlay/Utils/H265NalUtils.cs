using System;
using System.Collections.Generic;

namespace RemotePlay.Utils
{
    internal static class H265NalUtils
    {
        private static readonly byte[] StartCode3 = new byte[] { 0x00, 0x00, 0x01 };
        private static readonly byte[] StartCode4 = new byte[] { 0x00, 0x00, 0x00, 0x01 };

        /// <summary>
        /// 从 Annex B 帧中提取 VPS/SPS/PPS（带起始码）。
        /// 返回是否成功至少提取到 VPS+SPS+PPS 中的一种（通常需要全部）。
        /// </summary>
        public static bool TryExtractVpsSpsPps(byte[] annexBFrame, out byte[] vpsWithStartCode, out byte[] spsWithStartCode, out byte[] ppsWithStartCode)
        {
            vpsWithStartCode = Array.Empty<byte>();
            spsWithStartCode = Array.Empty<byte>();
            ppsWithStartCode = Array.Empty<byte>();
            if (annexBFrame == null || annexBFrame.Length < 6) return false;

            var nalUnits = SplitAnnexB(annexBFrame);
            foreach (var unit in nalUnits)
            {
                if (unit.Payload.Length < 2) continue;
                // HEVC nal_unit_type 位于第一个字节的 bit1..6（右移1，取6位）
                int nalType = (unit.Payload[0] >> 1) & 0x3F;
                if (nalType == 32 && vpsWithStartCode.Length == 0) // VPS
                {
                    vpsWithStartCode = Combine(unit.StartCode, unit.Payload);
                }
                else if (nalType == 33 && spsWithStartCode.Length == 0) // SPS
                {
                    spsWithStartCode = Combine(unit.StartCode, unit.Payload);
                }
                else if (nalType == 34 && ppsWithStartCode.Length == 0) // PPS
                {
                    ppsWithStartCode = Combine(unit.StartCode, unit.Payload);
                }
                if (vpsWithStartCode.Length > 0 && spsWithStartCode.Length > 0 && ppsWithStartCode.Length > 0)
                    return true;
            }
            return vpsWithStartCode.Length > 0 || spsWithStartCode.Length > 0 || ppsWithStartCode.Length > 0;
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


