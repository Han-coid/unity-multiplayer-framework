namespace Han.Multiplayer
{
    /// <summary>同网络根对象上的可选准入扩展；返回 null 放行，否则返回拒绝原因。</summary>
    /// <remarks>游戏 ID 与容量检查始终由核心执行，扩展不能绕过它们。</remarks>
    public interface IConnectionAdmissionRule
    {
        string CheckAdmission(ulong clientId);
    }
}
