using System;

namespace RemotePlay.Models.PlayStation
{
    /// <summary>
    /// Controller 状态（完整的输入状态）
    /// </summary>
    public class ControllerState
    {
        #region Button State
        
        /// <summary>
        /// 按键状态位掩码
        /// </summary>
        public ulong Buttons { get; set; }
        
        /// <summary>
        /// L2 扳机状态 (0-255)
        /// </summary>
        public byte L2State { get; set; }
        
        /// <summary>
        /// R2 扳机状态 (0-255)
        /// </summary>
        public byte R2State { get; set; }
        
        #endregion

        #region Stick State
        
        /// <summary>
        /// 左摇杆 X 轴 (-32768 ~ 32767)
        /// </summary>
        public short LeftX { get; set; }
        
        /// <summary>
        /// 左摇杆 Y 轴 (-32768 ~ 32767)
        /// </summary>
        public short LeftY { get; set; }
        
        /// <summary>
        /// 右摇杆 X 轴 (-32768 ~ 32767)
        /// </summary>
        public short RightX { get; set; }
        
        /// <summary>
        /// 右摇杆 Y 轴 (-32768 ~ 32767)
        /// </summary>
        public short RightY { get; set; }
        
        #endregion

        #region Motion Sensor State
        
        /// <summary>
        /// 陀螺仪 X 轴（角速度，单位：rad/s）
        /// </summary>
        public float GyroX { get; set; }
        
        /// <summary>
        /// 陀螺仪 Y 轴
        /// </summary>
        public float GyroY { get; set; }
        
        /// <summary>
        /// 陀螺仪 Z 轴
        /// </summary>
        public float GyroZ { get; set; }
        
        /// <summary>
        /// 加速度 X 轴（单位：g）
        /// </summary>
        public float AccelX { get; set; }
        
        /// <summary>
        /// 加速度 Y 轴
        /// </summary>
        public float AccelY { get; set; }
        
        /// <summary>
        /// 加速度 Z 轴
        /// </summary>
        public float AccelZ { get; set; }
        
        /// <summary>
        /// 方向四元数 X 分量
        /// </summary>
        public float OrientX { get; set; }
        
        /// <summary>
        /// 方向四元数 Y 分量
        /// </summary>
        public float OrientY { get; set; }
        
        /// <summary>
        /// 方向四元数 Z 分量
        /// </summary>
        public float OrientZ { get; set; }
        
        /// <summary>
        /// 方向四元数 W 分量
        /// </summary>
        public float OrientW { get; set; }
        
        #endregion

        #region Touchpad State (Optional)
        
        // TODO: 如果需要触摸板支持，添加触摸点数据
        
        #endregion

        #region Factory Methods
        
        /// <summary>
        /// 创建 Idle 状态（所有输入为 0）
        /// </summary>
        public static ControllerState CreateIdle()
        {
            return new ControllerState
            {
                Buttons = 0,
                L2State = 0,
                R2State = 0,
                LeftX = 0,
                LeftY = 0,
                RightX = 0,
                RightY = 0,
                GyroX = 0,
                GyroY = 0,
                GyroZ = 0,
                AccelX = 0,
                AccelY = 0,
                AccelZ = 1.0f,  // 重力加速度向下
                OrientX = 0,
                OrientY = 0,
                OrientZ = 0,
                OrientW = 1.0f  // 单位四元数
            };
        }
        
        #endregion

        #region Copy Helpers

        public ControllerState Clone()
        {
            return new ControllerState
            {
                Buttons = Buttons,
                L2State = L2State,
                R2State = R2State,
                LeftX = LeftX,
                LeftY = LeftY,
                RightX = RightX,
                RightY = RightY,
                GyroX = GyroX,
                GyroY = GyroY,
                GyroZ = GyroZ,
                AccelX = AccelX,
                AccelY = AccelY,
                AccelZ = AccelZ,
                OrientX = OrientX,
                OrientY = OrientY,
                OrientZ = OrientZ,
                OrientW = OrientW
            };
        }

        public void CopyFrom(ControllerState other)
        {
            ArgumentNullException.ThrowIfNull(other);

            Buttons = other.Buttons;
            L2State = other.L2State;
            R2State = other.R2State;
            LeftX = other.LeftX;
            LeftY = other.LeftY;
            RightX = other.RightX;
            RightY = other.RightY;
            GyroX = other.GyroX;
            GyroY = other.GyroY;
            GyroZ = other.GyroZ;
            AccelX = other.AccelX;
            AccelY = other.AccelY;
            AccelZ = other.AccelZ;
            OrientX = other.OrientX;
            OrientY = other.OrientY;
            OrientZ = other.OrientZ;
            OrientW = other.OrientW;
        }

        #endregion

        #region Equality
        
        public override bool Equals(object? obj)
        {
            if (obj is not ControllerState other) return false;
            
            return Buttons == other.Buttons
                && L2State == other.L2State
                && R2State == other.R2State
                && LeftX == other.LeftX
                && LeftY == other.LeftY
                && RightX == other.RightX
                && RightY == other.RightY
                && Math.Abs(GyroX - other.GyroX) < 0.0001f
                && Math.Abs(GyroY - other.GyroY) < 0.0001f
                && Math.Abs(GyroZ - other.GyroZ) < 0.0001f
                && Math.Abs(AccelX - other.AccelX) < 0.0001f
                && Math.Abs(AccelY - other.AccelY) < 0.0001f
                && Math.Abs(AccelZ - other.AccelZ) < 0.0001f
                && Math.Abs(OrientX - other.OrientX) < 0.0001f
                && Math.Abs(OrientY - other.OrientY) < 0.0001f
                && Math.Abs(OrientZ - other.OrientZ) < 0.0001f
                && Math.Abs(OrientW - other.OrientW) < 0.0001f;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(Buttons, LeftX, LeftY, RightX, RightY);
        }
        
        #endregion
    }
    
    /// <summary>
    /// Button masks（按键位掩码）
    /// </summary>
    public static class ControllerButtons
    {
        public const ulong CROSS = 0x0001;
        public const ulong CIRCLE = 0x0002;
        public const ulong SQUARE = 0x0004;
        public const ulong TRIANGLE = 0x0008;
        public const ulong DPAD_LEFT = 0x0010;
        public const ulong DPAD_UP = 0x0020;
        public const ulong DPAD_RIGHT = 0x0040;
        public const ulong DPAD_DOWN = 0x0080;
        public const ulong L1 = 0x0100;
        public const ulong R1 = 0x0200;
        public const ulong OPTIONS = 0x1000;
        public const ulong SHARE = 0x2000;
        public const ulong L3 = 0x4000;
        public const ulong R3 = 0x8000;
        public const ulong PS = 0x10000;
        public const ulong TOUCHPAD = 0x100000;
    }
    
    // 保留旧的 InputState 以便兼容
    public class InputState
    {
        public byte[] ToBytes()
        {
            return Array.Empty<byte>();
        }
    }
}
