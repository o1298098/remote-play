using System;

namespace RemotePlay.Services.Streaming.Controller
{
    /// <summary>
    /// 主机下发的 DualSense 自适应扳机效果，可直接填入手柄输出报告的扳机效果字段
    /// </summary>
    public sealed class TriggerEffectsEventArgs : EventArgs
    {
        public TriggerEffectsEventArgs(byte typeLeft, byte[] left, byte typeRight, byte[] right, byte intensity)
        {
            TypeLeft = typeLeft;
            Left = left;
            TypeRight = typeRight;
            Right = right;
            Intensity = intensity;
        }

        public byte TypeLeft { get; }

        /// <summary>
        /// 左扳机效果参数（10 字节）
        /// </summary>
        public byte[] Left { get; }

        public byte TypeRight { get; }

        /// <summary>
        /// 右扳机效果参数（10 字节）
        /// </summary>
        public byte[] Right { get; }

        /// <summary>
        /// 输出报告第 36 字节：高 4 位为扳机强度，低 4 位为震动强度（与 chiaki-ng 一致）
        /// </summary>
        public byte Intensity { get; }
    }

    /// <summary>
    /// 客户端上报的一个触摸板手指槽位（DualSense 原始数据）
    /// </summary>
    public sealed class TouchpadSlotInput
    {
        public bool Down { get; set; }

        /// <summary>
        /// 手柄的触点计数（7 位），变化表示这是一次新的触摸
        /// </summary>
        public int Contact { get; set; }

        public int X { get; set; }
        public int Y { get; set; }
    }

    /// <summary>
    /// 主机下发的光条颜色和玩家编号
    /// </summary>
    public sealed class LightbarEventArgs : EventArgs
    {
        public LightbarEventArgs(byte red, byte green, byte blue, byte playerIndex)
        {
            Red = red;
            Green = green;
            Blue = blue;
            PlayerIndex = playerIndex;
        }

        public byte Red { get; }
        public byte Green { get; }
        public byte Blue { get; }
        public byte PlayerIndex { get; }
    }
}
