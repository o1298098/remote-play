using System;

namespace RemotePlay.Services.Streaming.Controller
{
    /// <summary>
    /// 表示从 PlayStation 收到的震动反馈事件。
    /// </summary>
    public sealed class RumbleEventArgs : EventArgs
    {
        public RumbleEventArgs(
            byte unknown,
            byte left,
            byte right,
            byte adjustedLeft,
            byte adjustedRight,
            double multiplier,
            int ps5RumbleIntensity,
            int ps5TriggerIntensity)
        {
            Unknown = unknown;
            Left = left;
            Right = right;
            AdjustedLeft = adjustedLeft;
            AdjustedRight = adjustedRight;
            Multiplier = multiplier;
            Ps5RumbleIntensity = ps5RumbleIntensity;
            Ps5TriggerIntensity = ps5TriggerIntensity;
            TimestampUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// 原始包中的未知标志位。
        /// </summary>
        public byte Unknown { get; }

        /// <summary>
        /// 原始低频电机强度（0-255）。
        /// </summary>
        public byte Left { get; }

        /// <summary>
        /// 原始高频电机强度（0-255）。
        /// </summary>
        public byte Right { get; }

        /// <summary>
        /// 根据当前 PS5 震动强度映射后的低频电机强度。
        /// </summary>
        public byte AdjustedLeft { get; }

        /// <summary>
        /// 根据当前 PS5 震动强度映射后的高频电机强度。
        /// </summary>
        public byte AdjustedRight { get; }

        /// <summary>
        /// 当前用于非 DualSense 控制器的缩放倍率。
        /// </summary>
        public double Multiplier { get; }

        /// <summary>
        /// 当前 PS5 返回的震动强度值（-1 表示关闭）。
        /// </summary>
        public int Ps5RumbleIntensity { get; }

        /// <summary>
        /// 当前 PS5 返回的自适应扳机强度值（-1 表示关闭）。
        /// </summary>
        public int Ps5TriggerIntensity { get; }

        /// <summary>
        /// 事件产生的时间（UTC）。
        /// </summary>
        public DateTime TimestampUtc { get; }
    }
}

