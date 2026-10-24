using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace DotAsset
{
    // The C# type behind a SerializedProperty, found by walking the property path over the target's fields.
    // Works for script fields. Native fields (the m_ fields of built-in components) have no C# field and return null.
    public static class FieldTypes
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static Type Of(SerializedProperty prop)
        {
            if (prop == null || prop.serializedObject.targetObject == null)
            {
                return null;
            }
            Type current = prop.serializedObject.targetObject.GetType();
            string[] parts = prop.propertyPath.Split('.');
            string walked = null;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (current == null)
                {
                    return null;
                }
                if (part == "Array" && i + 1 < parts.Length && parts[i + 1].StartsWith("data[", StringComparison.Ordinal))
                {
                    current = ElementType(current);
                    walked = walked + ".Array." + parts[i + 1];
                    i++;
                    current = ManagedOverride(prop, walked, current);
                    continue;
                }
                FieldInfo field = FindField(current, part);
                if (field == null)
                {
                    return null;
                }
                current = field.FieldType;
                walked = walked == null ? part : walked + "." + part;
                current = ManagedOverride(prop, walked, current);
            }
            return current;
        }

        // Under [SerializeReference] the declared type is a base, the stored instance decides.
        static Type ManagedOverride(SerializedProperty prop, string path, Type declared)
        {
            if (declared == null || declared.IsValueType || declared == typeof(string) || typeof(UnityEngine.Object).IsAssignableFrom(declared))
            {
                return declared;
            }
            if (path == prop.propertyPath)
            {
                return declared;
            }
            var p = prop.serializedObject.FindProperty(path);
            if (p != null && p.propertyType == SerializedPropertyType.ManagedReference)
            {
                object value = p.managedReferenceValue;
                if (value != null)
                {
                    return value.GetType();
                }
            }
            return declared;
        }

        public static Type ElementType(Type t)
        {
            if (t == null)
            {
                return null;
            }
            if (t.IsArray)
            {
                return t.GetElementType();
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                return t.GetGenericArguments()[0];
            }
            return null;
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                FieldInfo f = cur.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    return f;
                }
            }
            return null;
        }

        // The object type a PPtr property accepts: the C# field type when there is one, else the type name Unity reports.
        public static Type ObjectReferenceType(SerializedProperty prop)
        {
            Type t = Of(prop);
            if (t != null && typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                return t;
            }
            string typeName = prop.type;
            if (typeName != null && typeName.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                string error;
                Type found = TypeResolver.Find(typeName, typeof(UnityEngine.Object), out error);
                if (found != null)
                {
                    return found;
                }
            }
            return typeof(UnityEngine.Object);
        }
    }
}
