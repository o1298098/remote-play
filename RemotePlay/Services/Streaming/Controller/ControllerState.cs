using System;
using System.Buffers.Binary;

namespace RemotePlay.Services.Streaming.Controller
{
    /// <summary>
    /// 完整的控制器状态，包含按键、摇杆、扳机、陀螺仪、加速度计等所有输入数据
    /// </summary>
    public class ControllerState
    {
        #region 按键状态（32位掩码）
        
        /// <summary>
        /// 按键状态位掩码
        /// </summary>
        public ulong Buttons { get; set; }
        
        // 按键常量
        public const ulong BUTTON_CROSS = 0x0001;
        public const ulong BUTTON_MOON = 0x0002;     // Circle
        public const ulong BUTTON_BOX = 0x0004;      // Square
        public const ulong BUTTON_PYRAMID = 0x0008;  // Triangle
        public const ulong BUTTON_DPAD_LEFT = 0x0010;
        public const ulong BUTTON_DPAD_UP = 0x0020;
        public const ulong BUTTON_DPAD_RIGHT = 0x0040;
        public const ulong BUTTON_DPAD_DOWN = 0x0080;
        public const ulong BUTTON_L1 = 0x0100;
        public const ulong BUTTON_R1 = 0x0200;
        public const ulong BUTTON_OPTIONS = 0x1000;
        public const ulong BUTTON_SHARE = 0x2000;
        public const ulong BUTTON_L3 = 0x4000;
        public const ulong BUTTON_R3 = 0x8000;
        public const ulong BUTTON_PS = 0x10000;
        public const ulong BUTTON_TOUCHPAD = 0x100000;
        
        #endregion
        
        #region 摇杆和扳机
        
        /// <summary>
        /// 左摇杆 X 轴（-32767 到 32767）
        /// </summary>
        public short LeftX { get; set; }
        
        /// <summary>
        /// 左摇杆 Y 轴（-32767 到 32767）
        /// </summary>
        public short LeftY { get; set; }
        
        /// <summary>
        /// 右摇杆 X 轴（-32767 到 32767）
        /// </summary>
        public short RightX { get; set; }
        
        /// <summary>
        /// 右摇杆 Y 轴（-32767 到 32767）
        /// </summary>
        public short RightY { get; set; }
        
        /// <summary>
        /// L2 扳机状态（0 到 255）
        /// </summary>
        public byte L2State { get; set; }
        
        /// <summary>
        /// R2 扳机状态（0 到 255）
        /// </summary>
        public byte R2State { get; set; }
        
        #endregion
        
        #region 陀螺仪（gyro）
        
        /// <summary>
        /// 陀螺仪 X 轴（角速度，单位：度/秒）
        /// 范围：-30.0 到 30.0
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
        
        #endregion
        
        #region 加速度计（accel）
        
        /// <summary>
        /// 加速度计 X 轴（单位：g）
        /// 范围：-5.0 到 5.0
        /// </summary>
        public float AccelX { get; set; }
        
        /// <summary>
        /// 加速度计 Y 轴
        /// </summary>
        public float AccelY { get; set; }

        /// <summary>
        /// 加速度计 Z 轴
        /// </summary>
        public float AccelZ { get; set; }
        
        #endregion
        
        #region 方向四元数（orientation）
        
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
        
        #region 触摸板（未实现）
        
        // TODO: 触摸板支持
        // public TouchpadState Touchpad { get; set; }
        
        #endregion
        
        #region 兼容旧代码的属性和方法
        
        /// <summary>
        /// 左摇杆状态（兼容旧代码）
        /// </summary>
        public StickState Left
        {
            get => new StickState(LeftX / 32767.0f, LeftY / 32767.0f);
            set
        {
                LeftX = value.GetXShort();
                LeftY = value.GetYShort();
        }
        }
        
        /// <summary>
        /// 右摇杆状态（兼容旧代码）
        /// </summary>
        public StickState Right
        {
            get => new StickState(RightX / 32767.0f, RightY / 32767.0f);
            set
        {
                RightX = value.GetXShort();
                RightY = value.GetYShort();
            }
        }

        /// <summary>
        /// 检查控制器状态是否为"空"（所有值都是默认值）
        /// </summary>
        public bool IsEmpty()
        {
            return Buttons == 0 && 
                   LeftX == 0 && LeftY == 0 && 
                   RightX == 0 && RightY == 0 &&
                   L2State == 0 && R2State == 0;
        }

        #region Copy Helpers

        public ControllerState Clone()
        {
            return new ControllerState
            {
                Buttons = Buttons,
                LeftX = LeftX,
                LeftY = LeftY,
                RightX = RightX,
                RightY = RightY,
                L2State = L2State,
                R2State = R2State,
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
            LeftX = other.LeftX;
            LeftY = other.LeftY;
            RightX = other.RightX;
            RightY = other.RightY;
            L2State = other.L2State;
            R2State = other.R2State;
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

        /// <summary>
        /// 打包为字节数组（用于FeedbackState）
        /// PS4: 25字节，PS5: 28字节
        /// 注意：这是旧代码兼容方法，新的FeedbackSender使用自己的格式化逻辑
        /// </summary>
        public byte[] Pack(bool isPs5)
        {
            // 运动传感器空闲数据（17字节，来自Python的_MOTION_IDLE）
            byte[] MotionIdle = new byte[] {
                0xA0, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF, 0x7F, 0xFF,
                0x7F, 0x99, 0x99, 0xFF, 0x7F, 0xFE, 0xF7, 0xEF,
                0x1F
            };
            
            int stateLength = isPs5 ? 28 : 25;
            var state = new byte[stateLength];

            // 复制运动传感器数据（偏移0，长度17）
                System.Buffer.BlockCopy(MotionIdle, 0, state, 0, 17);

            // 左摇杆 X/Y（各2字节，big-endian）
            BinaryPrimitives.WriteInt16BigEndian(state.AsSpan(17, 2), LeftX);
            BinaryPrimitives.WriteInt16BigEndian(state.AsSpan(19, 2), LeftY);

            // 右摇杆 X/Y（各2字节，big-endian）
            BinaryPrimitives.WriteInt16BigEndian(state.AsSpan(21, 2), RightX);
            BinaryPrimitives.WriteInt16BigEndian(state.AsSpan(23, 2), RightY);

            if (isPs5)
            {
                // PS5额外的3字节（offset 25-27）
                state[27] = 0x01; // DS4 模式
            }

            return state;
        }

        #endregion
        
        #region 构造函数
        
        /// <summary>
        /// 默认构造函数
        /// </summary>
        public ControllerState()
        {
            // 初始化为空闲状态
            OrientW = 1.0f;  // W=1 表示无旋转
        }
        
        /// <summary>
        /// 从旧的 StickState 创建（兼容旧代码）
        /// </summary>
        public ControllerState(StickState left, StickState right) : this()
        {
            Left = left;
            Right = right;
        }
        
        #endregion
        
        #region 工厂方法
        
        /// <summary>
        /// 创建空闲（idle）状态的控制器
        /// 所有摇杆居中，所有按键释放，陀螺仪/加速度为0
        /// </summary>
        public static ControllerState CreateIdle()
        {
            return new ControllerState();
        }
        
        #endregion
        
        #region 比较方法
        
        /// <summary>
        /// 判断两个状态是否相同
        /// </summary>
        public bool Equals(ControllerState? other)
        {
            if (other == null) return false;
            
            // 按键
            if (Buttons != other.Buttons) return false;
            
            // 摇杆
            if (LeftX != other.LeftX || LeftY != other.LeftY) return false;
            if (RightX != other.RightX || RightY != other.RightY) return false;
            
            // 扳机
            if (L2State != other.L2State || R2State != other.R2State) return false;
            
            // 陀螺仪
            const float epsilon = 0.0001f;
            if (Math.Abs(GyroX - other.GyroX) > epsilon) return false;
            if (Math.Abs(GyroY - other.GyroY) > epsilon) return false;
            if (Math.Abs(GyroZ - other.GyroZ) > epsilon) return false;
            
            // 加速度
            if (Math.Abs(AccelX - other.AccelX) > epsilon) return false;
            if (Math.Abs(AccelY - other.AccelY) > epsilon) return false;
            if (Math.Abs(AccelZ - other.AccelZ) > epsilon) return false;
            
            // 方向
            if (Math.Abs(OrientX - other.OrientX) > epsilon) return false;
            if (Math.Abs(OrientY - other.OrientY) > epsilon) return false;
            if (Math.Abs(OrientZ - other.OrientZ) > epsilon) return false;
            if (Math.Abs(OrientW - other.OrientW) > epsilon) return false;
            
            return true;
        }

        public override bool Equals(object? obj)
        {
            return obj is ControllerState other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Buttons);
            hash.Add(LeftX);
            hash.Add(LeftY);
            hash.Add(RightX);
            hash.Add(RightY);
            hash.Add(L2State);
            hash.Add(R2State);
            return hash.ToHashCode();
        }
        
        #endregion
    }
    
    /// <summary>
    /// Feedback Header 类型
    /// </summary>
    public enum FeedbackHeaderType : ushort
    {
        EVENT = 0,    // 0x00 - 按键事件（旧代码兼容）
        HISTORY = 1,  // 0x01 - 按键历史事件
        STATE = 6     // 0x06 - 控制器完整状态（摇杆+陀螺仪）
    }

    /// <summary>
    /// 反馈事件（按键事件）
    /// 对应Python的FeedbackEvent类
    /// </summary>
    public class FeedbackEvent
    {
        public const int Length = 3; // 按键事件固定长度为 3 字节
        private const byte PREFIX = 0x80; // 固定前缀标识

        /// <summary>
        /// 按键类型（对应旧版协议中的 FeedbackEvent.Type）
        /// </summary>
        public enum ButtonType : byte
        {
            UP = 0x80,
            DOWN = 0x81,
            LEFT = 0x82,
            RIGHT = 0x83,
            L1 = 0x84,
            R1 = 0x85,
            L2 = 0x86,
            R2 = 0x87,
            CROSS = 0x88,
            CIRCLE = 0x89,
            SQUARE = 0x8A,
            TRIANGLE = 0x8B,
            OPTIONS = 0x8C,
            SHARE = 0x8D,
            PS = 0x8E,
            L3 = 0x8F,
            R3 = 0x90,
            TOUCHPAD = 0x91
        }

        public ButtonType Type { get; set; }
        public bool IsActive { get; set; }
        private byte? _buttonId; // 缓存 button_id，用于某些按钮的特殊处理

        public FeedbackEvent(ButtonType type, bool isActive = true)
        {
            Type = type;
            IsActive = isActive;
        }

        /// <summary>
        /// 获取按钮 ID
        /// 对于 OPTIONS (0x8C) 及以上的按钮，按下时 button_id = button_id + 32
        /// </summary>
        private byte GetButtonId()
        {
            if (_buttonId.HasValue)
                return _buttonId.Value;

            byte buttonId = (byte)Type;
            if (buttonId >= 0x8C && IsActive)
            {
                _buttonId = (byte)(buttonId + 32);
            }
            else
            {
                _buttonId = buttonId;
            }
            return _buttonId.Value;
        }

        /// <summary>
        /// 打包为3字节数组
        /// 格式：[PREFIX: 1字节 (0x80)] [button_id: 1字节] [state: 1字节 (0xFF=按下, 0x00=释放)]
        /// </summary>
        public void Pack(byte[] buffer)
        {
            if (buffer.Length < Length)
                throw new ArgumentException($"Buffer must be at least {Length} bytes", nameof(buffer));

            byte buttonId = GetButtonId();
            byte state = (byte)(IsActive ? 0xFF : 0x00); // 0xFF 表示按下，0x00 表示释放

            buffer[0] = PREFIX;      // 0x80
            buffer[1] = buttonId;    // 按钮 ID
            buffer[2] = state;       // 状态：0xFF (按下) 或 0x00 (释放)
        }

        /// <summary>
        /// 打包为字节数组
        /// </summary>
        public byte[] Pack()
        {
            var buffer = new byte[Length];
            Pack(buffer);
            return buffer;
        }
    }

    /// <summary>
    /// 摇杆状态（兼容旧代码）
    /// </summary>
    public struct StickState
    {
        public float X { get; set; }
        public float Y { get; set; }

        public StickState(float x = 0.0f, float y = 0.0f)
        {
            X = Math.Clamp(x, -1.0f, 1.0f);
            Y = Math.Clamp(y, -1.0f, 1.0f);
        }

        /// <summary>
        /// 将浮点值（-1.0到1.0）转换为短整型（-32767到32767）
        /// </summary>
        public short GetXShort() => (short)(X * 32767);
        public short GetYShort() => (short)(Y * 32767);

        public bool Equals(StickState other)
        {
            return Math.Abs(X - other.X) < 0.001f && Math.Abs(Y - other.Y) < 0.001f;
        }

        public override bool Equals(object? obj)
        {
            return obj is StickState other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y);
        }

        public static bool operator ==(StickState left, StickState right) => left.Equals(right);
        public static bool operator !=(StickState left, StickState right) => !left.Equals(right);
    }
}
