/* This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 *
 * As a special exception to the Mozilla Public License, version 2.0, if you
 * compile your application source code and portions of this software are
 * embedded into the generated object code or executable form as a normal
 * consequence of the compilation process (such as inline functions,
 * templates, generics, or macros), you may redistribute such embedded portions
 * in such object code or executable form without complying with the source code
 * availability requirements or notice obligations of Section 3 of the MPL 2.0.
 */

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using BjoString;

namespace Bjolang.Runtime;

// The delegates and defaults of (std interop).
//
// A Bjolang function is a Func, and a function that returns void returns Unit.
// .NET events and many APIs want another delegate type: EventHandler<T>,
// Action<T>, or a delegate of their own. `Convert` makes a delegate of that
// type that calls the function.
//
// Each parameter is converted to the parameter type of the function, and the
// result to the result type of the delegate. A delegate that returns void
// drops the result, so a Bjolang function that returns Unit fits it.
//
// The converter for one pair of types is compiled once and kept, because a
// GUI makes many handlers of the same types.
[NonUserCode]
public static class BjoInterop
{
    private static readonly ConcurrentDictionary<(Type, Type), Func<Delegate, Delegate>> Converters = new();

    // A method group would make a new delegate at each call.
    private static readonly Func<(Type, Type), Func<Delegate, Delegate>> Builder = Build;

    /// A delegate of `delegateType` that calls `f`.
    public static Delegate Convert<F>(Type delegateType, F f)
    {
        if (f is not Delegate d)
            throw new ArgumentException($"as-delegate: {typeof(F)} is not a function");
        if (!typeof(Delegate).IsAssignableFrom(delegateType) || delegateType.IsAbstract)
            throw new ArgumentException($"as-delegate: {delegateType} is not a delegate type");
        if (d.GetType() == delegateType)
            return d;
        return Converters.GetOrAdd((delegateType, d.GetType()), Builder)(d);
    }

    /// The default value of `T`: null for a reference type and for `T?`,
    /// zero for a number. Bjolang has no literal for null, and some .NET
    /// parameters want one.
    public static T Default<T>() => default!;

    /// A delegate of type `D` that calls `f`. The call site gives `D`.
    public static D As<F, D>(F f) => (D)(object)Convert(typeof(D), f);

    /// Adds `f` to the CLR event `name` of `target`. Returns the delegate,
    /// because `RemoveEventHandler` needs the same one.
    public static Delegate AddEventHandler<T, F>(T target, Utf8String name, F f)
    {
        var ev = FindEvent(target, name);
        var handler = Convert(ev.EventHandlerType!, f);
        ev.AddEventHandler(target, handler);
        return handler;
    }

    /// Removes a delegate that `AddEventHandler` added.
    public static void RemoveEventHandler<T>(T target, Utf8String name, Delegate handler) =>
        FindEvent(target, name).RemoveEventHandler(target, handler);

    private static EventInfo FindEvent<T>(T target, Utf8String name)
    {
        if (target is null)
            throw new ArgumentNullException(nameof(target));
        return target.GetType().GetEvent(name.ToString(), BindingFlags.Public | BindingFlags.Instance)
               ?? throw new ArgumentException($"{target.GetType()} has no event {name}");
    }

    // Makes `source => (p1, ..., pn) => (R)((S)source).Invoke((Q1)p1, ..., (Qn)pn)`.
    private static Func<Delegate, Delegate> Build((Type, Type) key)
    {
        var (delegateType, sourceType) = key;
        var target = delegateType.GetMethod("Invoke")!;
        var source = sourceType.GetMethod("Invoke")!;
        var targetParams = target.GetParameters();
        var sourceParams = source.GetParameters();

        if (targetParams.Length != sourceParams.Length)
            throw new ArgumentException(
                $"as-delegate: {delegateType} takes {targetParams.Length} arguments, the function {sourceParams.Length}");

        // Loops and not LINQ: a lambda here would be user code to a debugger.
        var sourceParam = Expression.Parameter(typeof(Delegate), "source");
        var parameters = new ParameterExpression[targetParams.Length];
        var arguments = new Expression[targetParams.Length];
        for (var i = 0; i < targetParams.Length; i++)
        {
            var type = targetParams[i].ParameterType;
            if (type.IsByRef)
                throw new ArgumentException($"as-delegate: {delegateType} has a ref or out parameter");
            parameters[i] = Expression.Parameter(type, targetParams[i].Name);
            arguments[i] = Fit(parameters[i], sourceParams[i].ParameterType);
        }
        Expression body = Expression.Call(Expression.Convert(sourceParam, sourceType), source, arguments);

        if (target.ReturnType != typeof(void))
        {
            if (source.ReturnType == typeof(void) || source.ReturnType == typeof(Bjoml.Unit))
                throw new ArgumentException($"as-delegate: {delegateType} returns {target.ReturnType}, the function nothing");
            body = Fit(body, target.ReturnType);
        }

        // A lambda whose delegate returns void drops the value of its body.
        var inner = Expression.Lambda(delegateType, body, parameters);
        return Expression.Lambda<Func<Delegate, Delegate>>(inner, sourceParam).Compile();
    }

    private static Expression Fit(Expression e, Type type) =>
        e.Type == type ? e : Expression.Convert(e, type);
}
