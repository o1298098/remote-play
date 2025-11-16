using System;

namespace RemotePlay.Services.Streaming
{
	/// <summary>
	/// 16位序列号（0..65535）环绕安全比较与差值工具。
	/// 用于 RTP 序号、frameIndex、unitIndex 等 ushort 序列。
	/// 比较原则：将差值视为有符号16位整数进行正负判断。
	/// </summary>
	internal static class SequenceNumber
	{
		/// <summary>
		/// 判断 a 是否“小于” b（考虑回绕）。等价：(short)(a-b) &lt; 0
		/// </summary>
		public static bool Less(ushort a, ushort b)
		{
			return (short)(a - b) < 0;
		}

		/// <summary>
		/// 判断 a 是否“小于等于” b（考虑回绕）。
		/// </summary>
		public static bool LessOrEqual(ushort a, ushort b)
		{
			return a == b || (short)(a - b) < 0;
		}

		/// <summary>
		/// 计算 a 相对 b 的差值（a - b），返回 0..65535（考虑回绕）。
		/// 例如：Diff(0, 65530) = 6
		/// </summary>
		public static ushort Diff(ushort a, ushort b)
		{
			return (ushort)(a - b);
		}

		/// <summary>
		/// a 前进 step（考虑回绕）。
		/// </summary>
		public static ushort Advance(ushort a, int step = 1)
		{
			return (ushort)(a + step);
		}
	}
}

