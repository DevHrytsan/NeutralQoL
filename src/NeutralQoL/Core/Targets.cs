using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace NeutralQoL.Core
{
    public static class Targets
    {
        public static Type Type(string fullName)
        {
            return AccessTools.TypeByName(fullName) ?? throw new MissingMemberException($"type {fullName} missing");
        }

        public static MethodInfo Method(Type type, string name, Type[] parameters = null)
        {
            return AccessTools.Method(type, name, parameters) ?? throw new MissingMemberException($"{type.Name}.{name}() missing");
        }

        public static FieldInfo StaticField(Type type, string name)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || !field.IsStatic)
                throw new MissingMemberException($"static {type.Name}.{name} missing");
            return field;
        }

        // Reads a private-enum field as an int. FieldRefAccess refuses that type
        // mismatch, plain IL doesn't care, and the delegate doesn't box, so it is
        // fine every frame.
        public static Func<TOwner, int> EnumField<TOwner>(string name)
        {
            var field = AccessTools.Field(typeof(TOwner), name) ?? throw new MissingMemberException($"{typeof(TOwner).Name}.{name} missing");
            if (!field.FieldType.IsEnum || Enum.GetUnderlyingType(field.FieldType) != typeof(int))
                throw new MissingMemberException($"{typeof(TOwner).Name}.{name} is {field.FieldType.Name}, expected an int enum");
            var getter = new DynamicMethod($"get_{name}", typeof(int), new[] { typeof(TOwner) }, typeof(TOwner), true);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (Func<TOwner, int>)getter.CreateDelegate(typeof(Func<TOwner, int>));
        }

        public static AccessTools.FieldRef<TOwner, TField> Field<TOwner, TField>(string name)
        {
            var field = AccessTools.Field(typeof(TOwner), name) ?? throw new MissingMemberException($"{typeof(TOwner).Name}.{name} missing");
            if (!typeof(TField).IsAssignableFrom(field.FieldType))
                throw new MissingMemberException($"{typeof(TOwner).Name}.{name} is {field.FieldType.Name}, expected {typeof(TField).Name}");
            return AccessTools.FieldRefAccess<TOwner, TField>(field);
        }
    }
}
