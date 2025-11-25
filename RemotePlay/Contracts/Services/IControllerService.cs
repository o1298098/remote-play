using RemotePlay.Services.Streaming;
using RemotePlay.Services.Streaming.Controller;

namespace RemotePlay.Contracts.Services
{
    /// <summary>
    /// 控制器服务接口
    /// 对应Python的Controller类
    /// </summary>
    public interface IControllerService
    {
        /// <summary>
        /// 按键动作类型
        /// </summary>
        enum ButtonAction
        {
            PRESS,   // 按下
            RELEASE, // 释放
            TAP      // 轻按（按下后自动释放）
        }

        /// <summary>
        /// 连接控制器到会话
        /// </summary>
        Task<bool> ConnectAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// 断开控制器连接
        /// </summary>
        Task DisconnectAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// 启动控制器（开始自动发送摇杆状态）
        /// </summary>
        Task<bool> StartAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// 停止控制器
        /// </summary>
        Task StopAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// 按键操作
        /// </summary>
        Task ButtonAsync(
            Guid sessionId,
            FeedbackEvent.ButtonType button,
            ButtonAction action = ButtonAction.TAP,
            int delayMs = 100,
            CancellationToken ct = default);

        /// <summary>
        /// 设置摇杆状态
        /// </summary>
        Task StickAsync(
            Guid sessionId,
            string stickName,   // "left" 或 "right"
            string? axis = null,  // "x" 或 "y"
            float? value = null,  // -1.0 到 1.0
            (float x, float y)? point = null,  // 坐标点
            CancellationToken ct = default);

        /// <summary>
        /// 设置扳机压力（0 到 1）
        /// </summary>
        Task SetTriggersAsync(
            Guid sessionId,
            float? l2 = null,
            float? r2 = null,
            CancellationToken ct = default);

        /// <summary>
        /// 手动更新摇杆状态（如果未启动自动发送）
        /// </summary>
        Task UpdateSticksAsync(Guid sessionId, CancellationToken ct = default);

        /// <summary>
        /// 获取当前摇杆状态
        /// </summary>
        ControllerState? GetStickState(Guid sessionId);

        /// <summary>
        /// 检查控制器是否正在运行
        /// </summary>
        bool IsRunning(Guid sessionId);

        /// <summary>
        /// 检查控制器是否就绪
        /// </summary>
        bool IsReady(Guid sessionId);

        /// <summary>
        /// 获取所有可用的按键名称
        /// </summary>
        List<string> GetAvailableButtons();
    }
}

