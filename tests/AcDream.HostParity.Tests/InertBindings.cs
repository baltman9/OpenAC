using System.Reflection;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Tells a binding that does something apart from a binding that is there and
/// does nothing.
///
/// The census can see whether a host handed the runtime a delegate; until now
/// it could not see whether that delegate had a body. A host that passes
/// <c>_ =&gt; { }</c> looks, to every list and every claim, exactly like a host
/// that does the work -- and a plugin asking the question gets an answer that
/// is quietly always the same. That class of difference is what this finds.
///
/// The test is on the compiled body: a delegate whose method can reach nothing
/// outside itself -- no call, no allocation, no store to a field or through a
/// pointer, no throw -- cannot have an effect anyone can observe. For a
/// delegate that returns something, a body that reaches nothing may still be
/// answering a constant, so it counts as doing nothing only when it is short
/// enough to be exactly that: push a constant and return.
/// </summary>
internal static class InertBindings
{
    /// <summary>
    /// Instructions through which a method body can affect anything beyond its
    /// own evaluation stack. An inert body contains none of them, and none of
    /// them takes an operand, so looking for the bytes is exact for the bodies
    /// this is asked about: a body that does have operands contains at least
    /// one of these opcodes for real and is reported as doing something.
    /// </summary>
    private static readonly byte[] ReachOutside =
    [
        0x28, // call
        0x29, // calli
        0x6F, // callvirt
        0x73, // newobj
        0x7A, // throw
        0x7D, // stfld
        0x80, // stsfld
        0x8D, // newarr
        0xA2, // stelem
    ];

    /// <summary>
    /// The longest body that can only be answering a constant: at most one
    /// instruction to push the answer, one to return, and one debug no-op.
    /// </summary>
    private const int ConstantAnswerLength = 3;

    /// <summary>Whether this value is a delegate that cannot affect anything.</summary>
    internal static bool DoesNothing(object? value)
    {
        if (value is not Delegate binding)
            return false;
        foreach (Delegate part in binding.GetInvocationList())
        {
            if (!MethodDoesNothing(part.Method))
                return false;
        }
        return true;
    }

    private static bool MethodDoesNothing(MethodInfo method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            // Nothing to read -- an intrinsic or a native method. Assume it
            // acts rather than excuse it on no evidence.
            return false;
        }
        foreach (byte instruction in il)
        {
            if (ReachOutside.Contains(instruction))
                return false;
        }
        return method.ReturnType == typeof(void)
            || il.Length <= ConstantAnswerLength;
    }
}
