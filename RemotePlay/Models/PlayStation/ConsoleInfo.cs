namespace RemotePlay.Models.PlayStation
{
    public record ConsoleInfo(
        string Ip,
        string Name,
        string Uuid,
        string? HostType = null,
        string? SystemVerion = null,
        string? DeviceDiscoverPotocolVersion = null,
        string? status = null);
}
