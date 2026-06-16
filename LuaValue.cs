namespace Arbiter;

public sealed class LuaValue
{
    public override string ToString()
    {
        switch (Kind)
        {
            case ValueKind.String:
                return StringValue ?? "";

            case ValueKind.Number:
                return NumberValue?.ToString() ?? "0";

            case ValueKind.Boolean:
                return BooleanValue?.ToString().ToLowerInvariant() ?? "false";

            default:
                return "nil";
        }
    }

    public enum ValueKind
    {
        String,
        Number,
        Boolean
    }
    public ValueKind Kind { get; }
    public string? StringValue { get; }
    public double? NumberValue { get; }
    public bool? BooleanValue { get; }
    private LuaValue(ValueKind kind, string? str, double? num, bool? boolean)
    {
        Kind = kind;
        StringValue = str;
        NumberValue = num;
        BooleanValue = boolean;
    }
    public static LuaValue FromString(string value) => new(ValueKind.String, value, null, null);
    public static LuaValue FromNumber(double value) => new(ValueKind.Number, null, value, null);
    public static LuaValue FromBool(bool value) => new(ValueKind.Boolean, null, null, value);   
}