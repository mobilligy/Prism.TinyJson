using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;

namespace TinyJson
{
    // Really simple JSON parser in ~300 lines
    // - Attempts to parse JSON files with minimal GC allocation
    // - Nice and simple "[1,2,3]".FromJson<List<int>>() API
    // - Classes and structs can be parsed too!
    //      class Foo { public int Value; }
    //      "{\"Value\":10}".FromJson<Foo>()
    // - Can parse JSON without type information into Dictionary<string,object> and List<object> e.g.
    //      "[1,2,3]".FromJson<object>().GetType() == typeof(List<object>)
    //      "{\"Value\":10}".FromJson<object>().GetType() == typeof(Dictionary<string,object>)
    // - No JIT Emit support to support AOT compilation on iOS
    // - Attempts are made to NOT throw an exception if the JSON is corrupted or invalid: returns null instead.
    // - Only public fields and property setters on classes/structs will be written to
    //
    // Limitations:
    // - No JIT Emit support to parse structures quickly
    // - Limited to parsing <2GB JSON files (due to int.MaxValue)
    // - Parsing of abstract classes or interfaces is NOT supported and will throw an exception.
    public static class JSONParser
    {
        [ThreadStatic] static Stack<List<string>> splitArrayPool;
        [ThreadStatic] static StringBuilder stringBuilder;
        [ThreadStatic] static Dictionary<Type, Dictionary<string, FieldInfo>> fieldInfoCache;
        [ThreadStatic] static Dictionary<Type, Dictionary<string, PropertyInfo>> propertyInfoCache;

        public static T FromJson<T>(this string json)
        {
            // Initialize, if needed, the ThreadStatic variables
            if (propertyInfoCache == null) propertyInfoCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
            if (fieldInfoCache == null) fieldInfoCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
            if (stringBuilder == null) stringBuilder = new StringBuilder();
            if (splitArrayPool == null) splitArrayPool = new Stack<List<string>>();

            //Remove all whitespace not within strings to make parsing simpler
            stringBuilder.Length = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    i = AppendUntilStringEnd(true, i, json);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                    continue;

                stringBuilder.Append(c);
            }

            //Parse the thing!
            return (T)ParseValue(typeof(T), stringBuilder.ToString());
        }

        public static JsonToken FromJsonToToken<T>(this string json)
        {
            // Initialize, if needed, the ThreadStatic variables
            if (propertyInfoCache == null) propertyInfoCache = new Dictionary<Type, Dictionary<string, PropertyInfo>>();
            if (fieldInfoCache == null) fieldInfoCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
            if (stringBuilder == null) stringBuilder = new StringBuilder();
            if (splitArrayPool == null) splitArrayPool = new Stack<List<string>>();

            //Remove all whitespace not within strings to make parsing simpler
            stringBuilder.Length = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    i = AppendUntilStringEnd(true, i, json);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                    continue;

                stringBuilder.Append(c);
            }

            //Parse the thing!
            return ParseToken(typeof(T), stringBuilder.ToString());
        }

        static int AppendUntilStringEnd(bool appendEscapeCharacter, int startIdx, string json)
        {
            stringBuilder.Append(json[startIdx]);
            for (int i = startIdx + 1; i < json.Length; i++)
            {
                if (json[i] == '\\')
                {
                    if (appendEscapeCharacter)
                        stringBuilder.Append(json[i]);
                    stringBuilder.Append(json[i + 1]);
                    i++;//Skip next character as it is escaped
                }
                else if (json[i] == '"')
                {
                    stringBuilder.Append(json[i]);
                    return i;
                }
                else
                    stringBuilder.Append(json[i]);
            }
            return json.Length - 1;
        }

        //Splits { <value>:<value>, <value>:<value> } and [ <value>, <value> ] into a list of <value> strings
        static List<string> Split(string json)
        {
            List<string> splitArray = splitArrayPool.Count > 0 ? splitArrayPool.Pop() : new List<string>();
            splitArray.Clear();
            if (json.Length == 2)
                return splitArray;
            int parseDepth = 0;
            stringBuilder.Length = 0;
            for (int i = 1; i < json.Length - 1; i++)
            {
                switch (json[i])
                {
                    case '[':
                    case '{':
                        parseDepth++;
                        break;
                    case ']':
                    case '}':
                        parseDepth--;
                        break;
                    case '"':
                        i = AppendUntilStringEnd(true, i, json);
                        continue;
                    case ',':
                    case ':':
                        if (parseDepth == 0)
                        {
                            splitArray.Add(stringBuilder.ToString());
                            stringBuilder.Length = 0;
                            continue;
                        }
                        break;
                }

                stringBuilder.Append(json[i]);
            }

            splitArray.Add(stringBuilder.ToString());

            return splitArray;
        }

        internal static object ParseValue(Type type, string json)
        {
            if (type == typeof(string))
            {
                if (json.Length <= 2)
                    return string.Empty;
                StringBuilder parseStringBuilder = new StringBuilder(json.Length);
                for (int i = 1; i < json.Length - 1; ++i)
                {
                    if (json[i] == '\\' && i + 1 < json.Length - 1)
                    {
                        int j = "\"\\nrtbf/".IndexOf(json[i + 1]);
                        if (j >= 0)
                        {
                            parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
                            ++i;
                            continue;
                        }
                        if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
                        {
                            UInt32 c = 0;
                            if (UInt32.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out c))
                            {
                                parseStringBuilder.Append((char)c);
                                i += 5;
                                continue;
                            }
                        }
                    }
                    parseStringBuilder.Append(json[i]);
                }
                return parseStringBuilder.ToString();
            }
            if (type.IsPrimitive)
            {
                var result = Convert.ChangeType(json, type, System.Globalization.CultureInfo.InvariantCulture);
                return result;
            }
            if (type == typeof(decimal))
            {
                decimal result;
                decimal.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
                return result;
            }
            if (json == "null")
            {
                return null;
            }
            if (type.IsEnum)
            {
                if (json[0] == '"')
                    json = json.Substring(1, json.Length - 2);
                try
                {
                    return Enum.Parse(type, json, false);
                }
                catch
                {
                    return 0;
                }
            }
            if (type.IsArray)
            {
                Type arrayType = type.GetElementType();
                if (json[0] != '[' || json[json.Length - 1] != ']')
                    return null;

                List<string> elems = Split(json);
                Array newArray = Array.CreateInstance(arrayType, elems.Count);
                for (int i = 0; i < elems.Count; i++)
                    newArray.SetValue(ParseValue(arrayType, elems[i]), i);
                splitArrayPool.Push(elems);
                return newArray;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type listType = type.GetGenericArguments()[0];
                if (json[0] != '[' || json[json.Length - 1] != ']')
                    return null;

                List<string> elems = Split(json);
                var list = (IList)type.GetConstructor(new Type[] { typeof(int) }).Invoke(new object[] { elems.Count });
                for (int i = 0; i < elems.Count; i++)
                    list.Add(ParseValue(listType, elems[i]));
                splitArrayPool.Push(elems);
                return list;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                Type keyType, valueType;
                {
                    Type[] args = type.GetGenericArguments();
                    keyType = args[0];
                    valueType = args[1];
                }

                //Refuse to parse dictionary keys that aren't of type string
                if (keyType != typeof(string))
                    return null;
                //Must be a valid dictionary element
                if (json[0] != '{' || json[json.Length - 1] != '}')
                    return null;
                //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
                List<string> elems = Split(json);
                if (elems.Count % 2 != 0)
                    return null;

                var dictionary = (IDictionary)type.GetConstructor(new Type[] { typeof(int) }).Invoke(new object[] { elems.Count / 2 });
                for (int i = 0; i < elems.Count; i += 2)
                {
                    if (elems[i].Length <= 2)
                        continue;
                    string keyValue = elems[i].Substring(1, elems[i].Length - 2);
                    object val = ParseValue(valueType, elems[i + 1]);
                    dictionary.Add(keyValue, val);
                }
                return dictionary;
            }
            if (type == typeof(object))
            {
                return ParseAnonymousValue(json);
            }
            if (json[0] == '{' && json[json.Length - 1] == '}')
            {
                return ParseObject(type, json);
            }

            return null;
        }

        internal static JsonToken ParseToken(Type type, string json)
        {
            if (JSONUtilities.IsNullableType(type))
            {
                type = Nullable.GetUnderlyingType(type);
            }

            if (type == typeof(string))
            {
                if (json.Length <= 2)
                    return new JsonToken(JsonTokenType.String, String.Empty);

                StringBuilder parseStringBuilder = new StringBuilder(json.Length);
                for (int i = 1; i < json.Length - 1; ++i)
                {
                    if (json[i] == '\\' && i + 1 < json.Length - 1)
                    {
                        int j = "\"\\nrtbf/".IndexOf(json[i + 1]);
                        if (j >= 0)
                        {
                            parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
                            ++i;
                            continue;
                        }
                        if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
                        {
                            UInt32 c = 0;
                            if (UInt32.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out c))
                            {
                                parseStringBuilder.Append((char)c);
                                i += 5;
                                continue;
                            }
                        }
                    }
                    parseStringBuilder.Append(json[i]);
                }

                return new JsonToken(JsonTokenType.String, parseStringBuilder.ToString());
            }
            if (type.IsPrimitive)
            {
                var tokenType = GetTokenTypeForPrimitive(type);

                var result = new object();

                if (tokenType == JsonTokenType.Integer)
                {
                    result = Convert.ChangeType(json, typeof(long), System.Globalization.CultureInfo.InvariantCulture);
                }
                else
                {
                    result = Convert.ChangeType(json, type, System.Globalization.CultureInfo.InvariantCulture);
                }

                return new JsonToken(tokenType, result);
            }
            if (type == typeof(decimal))
            {
                float result;
                float.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);

                return new JsonToken(JsonTokenType.Float, result);
            }
            if (json == "null")
            {
                return new JsonToken(JsonTokenType.Null, null);
            }
            if (type.IsEnum)
            {
                if (json[0] == '"')
                    json = json.Substring(1, json.Length - 2);

                try
                {
                    if (long.TryParse(json, out var value))
                    {
                        return new JsonToken(JsonTokenType.Integer, value);
                    }
                    else
                    {
                        return new JsonToken(JsonTokenType.Integer, Convert.ToInt64(Enum.Parse(type, json, false)));
                    }
                }
                catch
                {
                    return new JsonToken(JsonTokenType.Integer, 0L);
                }
            }
            if (type == typeof(DateTime))
            {
                if (json[0] == '"')
                    json = json.Substring(1, json.Length - 2).Replace("\\", "");

                return new JsonToken(JsonTokenType.String, json);
            }
            if (type == typeof(Byte[]))
            {
                // treat this as a string
                if (json[0] == '"')
                    json = json.Substring(1, json.Length - 2);

                return new JsonToken(JsonTokenType.String, json);
            }
            if (type.IsArray)
            {
                Type arrayType = type.GetElementType();
                if (json[0] != '[' || json[json.Length - 1] != ']')
                    return new JsonToken(JsonTokenType.Null, null);

                List<string> elems = Split(json);

                var array = new JsonToken[elems.Count];

                for (int i = 0; i < elems.Count; i++)
                    array[i] = ParseToken(arrayType, elems[i]);

                splitArrayPool.Push(elems);

                return new JsonToken(JsonTokenType.Array, array);
            }
            if (type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>)))
            {
                var generic = type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>));

                Type listType = generic.GetGenericArguments()[0];
                if (json[0] != '[' || json[json.Length - 1] != ']')
                    return new JsonToken(JsonTokenType.Null, null);

                List<string> elems = Split(json);

                var array = new JsonToken[elems.Count];

                for (int i = 0; i < elems.Count; i++)
                    array[i] = ParseToken(listType, elems[i]);

                splitArrayPool.Push(elems);

                return new JsonToken(JsonTokenType.Array, array);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                Type keyType, valueType;
                {
                    Type[] args = type.GetGenericArguments();
                    keyType = args[0];
                    valueType = args[1];
                }

                //Refuse to parse dictionary keys that aren't of type string
                if (keyType != typeof(string))
                    return new JsonToken(JsonTokenType.Null, null);

                //Must be a valid dictionary element
                if (json[0] != '{' || json[json.Length - 1] != '}')
                    return new JsonToken(JsonTokenType.Null, null);

                //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
                List<string> elems = Split(json);
                if (elems.Count % 2 != 0)
                    return new JsonToken(JsonTokenType.Null, null);

                var token = new JsonToken(JsonTokenType.Object, null);

                for (int i = 0; i < elems.Count; i += 2)
                {
                    if (elems[i].Length <= 2)
                        continue;

                    string keyValue = elems[i].Substring(1, elems[i].Length - 2);

                    token.AddKey(keyValue, ParseToken(valueType, elems[i + 1]));
                }

                return token;
            }
            if (type == typeof(object))
            {
                return ParseAnonymousToken(json);
            }
            if (json[0] == '{' && json[json.Length - 1] == '}')
            {
                return ParseObjectToken(type, json);
            }

            return new JsonToken(JsonTokenType.Null, null);
        }

        static object ParseAnonymousValue(string json)
        {
            if (json.Length == 0)
                return null;
            if (json[0] == '{' && json[json.Length - 1] == '}')
            {
                List<string> elems = Split(json);
                if (elems.Count % 2 != 0)
                    return null;
                var dict = new Dictionary<string, object>(elems.Count / 2);
                for (int i = 0; i < elems.Count; i += 2)
                    dict.Add(elems[i].Substring(1, elems[i].Length - 2), ParseAnonymousValue(elems[i + 1]));
                return dict;
            }
            if (json[0] == '[' && json[json.Length - 1] == ']')
            {
                List<string> items = Split(json);
                var finalList = new List<object>(items.Count);
                for (int i = 0; i < items.Count; i++)
                    finalList.Add(ParseAnonymousValue(items[i]));
                return finalList;
            }
            if (json[0] == '"' && json[json.Length - 1] == '"')
            {
                string str = json.Substring(1, json.Length - 2);
                return str.Replace("\\", string.Empty);
            }
            if (char.IsDigit(json[0]) || json[0] == '-')
            {
                if (json.Contains("."))
                {
                    double result;
                    double.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
                    return result;
                }
                else
                {
                    int result;
                    int.TryParse(json, out result);
                    return result;
                }
            }
            if (json == "true")
                return true;
            if (json == "false")
                return false;
            // handles json == "null" as well as invalid JSON
            return null;
        }

        static JsonToken ParseAnonymousToken(string json)
        {
            if (json.Length == 0)
                return new JsonToken(JsonTokenType.Null, null);

            if (json[0] == '{' && json[json.Length - 1] == '}')
            {
                List<string> elems = Split(json);
                if (elems.Count % 2 != 0)
                    return new JsonToken(JsonTokenType.Null, null);

                var dict = new Dictionary<string, object>(elems.Count / 2);
                for (int i = 0; i < elems.Count; i += 2)
                    dict.Add(elems[i].Substring(1, elems[i].Length - 2), ParseAnonymousToken(elems[i + 1]));
                return new JsonToken(JsonTokenType.Object, dict);
            }
            if (json[0] == '[' && json[json.Length - 1] == ']')
            {
                List<string> items = Split(json);
                var finalList = new List<object>(items.Count);
                for (int i = 0; i < items.Count; i++)
                    finalList.Add(ParseAnonymousToken(items[i]));

                return new JsonToken(JsonTokenType.Array, finalList);
            }
            if (json[0] == '"' && json[json.Length - 1] == '"')
            {
                string str = json.Substring(1, json.Length - 2);

                return new JsonToken(JsonTokenType.String, str.Replace("\\", string.Empty));
            }
            if (char.IsDigit(json[0]) || json[0] == '-')
            {
                if (json.Contains("."))
                {
                    double result;
                    double.TryParse(json, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);

                    return new JsonToken(JsonTokenType.Float, result);
                }
                else
                {
                    int result;
                    int.TryParse(json, out result);

                    return new JsonToken(JsonTokenType.Integer, result);
                }
            }
            if (json == "true")
                return new JsonToken(JsonTokenType.Boolean, true);
            if (json == "false")
                return new JsonToken(JsonTokenType.Boolean, false);

            // handles json == "null" as well as invalid JSON
            return new JsonToken(JsonTokenType.Null, null);
        }

        static Dictionary<string, T> CreateMemberNameDictionary<T>(T[] members) where T : MemberInfo
        {
            Dictionary<string, T> nameToMember = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < members.Length; i++)
            {
                T member = members[i];
                if (member.IsDefined(typeof(JsonIgnoreAttribute), true) || member.IsDefined(typeof(IgnoreDataMemberAttribute), true))
                    continue;

                string name = member.Name;
                if (member.IsDefined(typeof(JsonPropertyAttribute), true))
                {
                    JsonPropertyAttribute memberAttribute = (JsonPropertyAttribute)Attribute.GetCustomAttribute(member, typeof(JsonPropertyAttribute), true);
                    if (!string.IsNullOrEmpty(memberAttribute.PropertyName))
                        name = memberAttribute.PropertyName;
                }

                nameToMember.Add(name, member);
            }

            return nameToMember;
        }

        static object ParseObject(Type type, string json)
        {
            object instance = FormatterServices.GetUninitializedObject(type);

            //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
            List<string> elems = Split(json);
            if (elems.Count % 2 != 0)
                return instance;

            Dictionary<string, FieldInfo> nameToField;
            Dictionary<string, PropertyInfo> nameToProperty;
            if (!fieldInfoCache.TryGetValue(type, out nameToField))
            {
                nameToField = CreateMemberNameDictionary(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
                fieldInfoCache.Add(type, nameToField);
            }
            if (!propertyInfoCache.TryGetValue(type, out nameToProperty))
            {
                nameToProperty = CreateMemberNameDictionary(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
                propertyInfoCache.Add(type, nameToProperty);
            }

            for (int i = 0; i < elems.Count; i += 2)
            {
                if (elems[i].Length <= 2)
                    continue;
                string key = elems[i].Substring(1, elems[i].Length - 2);
                string value = elems[i + 1];

                FieldInfo fieldInfo;
                PropertyInfo propertyInfo;
                if (nameToField.TryGetValue(key, out fieldInfo))
                    fieldInfo.SetValue(instance, ParseValue(fieldInfo.FieldType, value));
                else if (nameToProperty.TryGetValue(key, out propertyInfo))
                    propertyInfo.SetValue(instance, ParseValue(propertyInfo.PropertyType, value), null);
            }

            return instance;
        }

        static JsonToken ParseObjectToken(Type type, string json)
        {
            object instance = FormatterServices.GetUninitializedObject(type);

            //The list is split into key/value pairs only, this means the split must be divisible by 2 to be valid JSON
            List<string> elems = Split(json);
            if (elems.Count % 2 != 0)
                return new JsonToken(JsonTokenType.Null, null);

            var token = new JsonToken(JsonTokenType.Object, null);

            Dictionary<string, FieldInfo> nameToField;
            Dictionary<string, PropertyInfo> nameToProperty;
            if (!fieldInfoCache.TryGetValue(type, out nameToField))
            {
                nameToField = CreateMemberNameDictionary(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
                fieldInfoCache.Add(type, nameToField);
            }
            if (!propertyInfoCache.TryGetValue(type, out nameToProperty))
            {
                nameToProperty = CreateMemberNameDictionary(type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy));
                propertyInfoCache.Add(type, nameToProperty);
            }

            for (int i = 0; i < elems.Count; i += 2)
            {
                if (elems[i].Length <= 2)
                    continue;
                string key = elems[i].Substring(1, elems[i].Length - 2);
                string value = elems[i + 1];

                FieldInfo fieldInfo;
                PropertyInfo propertyInfo;

                if (nameToField.TryGetValue(key, out fieldInfo))
                {
                    token.AddKey(key, ParseToken(fieldInfo.FieldType, value));
                }
                else if (nameToProperty.TryGetValue(key, out propertyInfo))
                {
                    if (propertyInfo != null)
                    {
                        token.AddKey(key, ParseToken(propertyInfo.PropertyType, value));
                    }
                    else
                    {
                    }
                }
            }

            return token;
        }

        public static JsonToken GetTopObjectWithProperty<T>(StreamReader reader, string propertyName, string propertyValue, int depth = 1)
        {
            var parseDepth = 0;
            var topDepth = depth - 1;

            var stringBuilder = new StringBuilder();
            var topStringBuilder = new StringBuilder();

            var property = default(string);
            var value = default(string);

            var topValue = default(string);

            var inString = false;
            var inValue = false;

            var inTopString = false;

            var found = false;

            while (reader.Peek() >= 0)
            {
                var c = (char)reader.Read();

                if (c == '\\' && inString == false)
                {
                    // get the next character instead.
                    if (reader.Peek() >= 0)
                    {
                        c = (char)reader.Read();
                    }
                }

                switch (c)
                {
                    case '[':
                    case '{':
                        if (parseDepth == topDepth)
                        {
                            // start saving these off
                            inTopString = true;
                        }

                        parseDepth++;

                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }
                        break;
                    case ']':
                    case '}':
                        if (inValue && parseDepth == depth)
                        {
                            value = stringBuilder.ToString();
                            stringBuilder.Clear();

                            inValue = false;
                            inString = false;
                        }

                        parseDepth--;

                        if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }

                        if (inTopString && parseDepth == topDepth)
                        {
                            topValue = topStringBuilder.ToString();
                            topStringBuilder.Clear();

                            inTopString = false;
                        }

                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        break;
                    case '"':
                        if (parseDepth == depth &&
                            inValue == false)
                        {
                            if (inString)
                            {
                                property = stringBuilder.ToString();
                                stringBuilder.Clear();

                                value = null;

                                inString = false;
                            }
                            else
                            {
                                // check the object!
                                inString = true;
                            }
                        }
                        else if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }

                        break;
                    case ',':
                        if (inValue && parseDepth == depth)
                        {
                            value = stringBuilder.ToString();
                            stringBuilder.Clear();

                            inValue = false;
                            inString = false;
                        }

                        if (inTopString && parseDepth == topDepth)
                        {
                            topValue = topStringBuilder.ToString();
                            topStringBuilder.Clear();

                            inTopString = false;
                        }
                        else if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }

                        break;
                    case ':':
                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }
                        else
                        {
                            if (parseDepth == depth &&
                                property == propertyName &&
                                inValue == false)
                            {
                                inValue = true;
                                inString = true;
                            }
                        }

                        if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }

                        break;
                    default:
                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        if (inTopString)
                        {
                            topStringBuilder.Append(c);
                        }

                        break;
                }

                if (property == propertyName &&
                    string.IsNullOrEmpty(value) == false &&
                    ParseString(value) == propertyValue)
                {
                    // we're where we need to be! But we need to wait until we have the whole upper object...
                    found = true;
                }

                if (found &&
                    inTopString == false &&
                    string.IsNullOrEmpty(topValue) == false)
                {
                    return ParseToken(typeof(T), topValue);
                }
            }

            return new JsonToken(JsonTokenType.Null, null);
        }

        public static JsonToken ParseSpecificValue<T>(StreamReader reader, string propertyName, int depth = 1)
        {
            int parseDepth = 0;

            var stringBuilder = new StringBuilder();

            var property = default(string);
            var value = default(string);

            var inString = false;
            var inValue = false;

            while (reader.Peek() >= 0)
            {
                var c = (char)reader.Read();

                if (c == '\\' && inString == false)
                {
                    // get the next character instead.
                    if (reader.Peek() >= 0)
                    {
                        c = (char)reader.Read();
                    }
                }

                switch (c)
                {
                    case '[':
                    case '{':
                        parseDepth++;

                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }
                        break;
                    case ']':
                    case '}':
                        if (inValue && parseDepth == depth)
                        {
                            value = stringBuilder.ToString();
                            stringBuilder.Clear();

                            inValue = false;
                            inString = false;
                        }

                        parseDepth--;

                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }
                        break;
                    case '"':
                        if (parseDepth == depth &&
                            inValue == false)
                        {
                            if (inString)
                            {
                                property = stringBuilder.ToString();
                                stringBuilder.Clear();

                                value = null;

                                inString = false;
                            }
                            else
                            {
                                // check the object!
                                inString = true;
                            }
                        }
                        else if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        break;
                    case ',':
                        if (inValue && parseDepth == depth)
                        {
                            value = stringBuilder.ToString();
                            stringBuilder.Clear();

                            inValue = false;
                            inString = false;
                        }
                        break;
                    case ':':
                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }
                        else
                        {
                            if (parseDepth == depth &&
                                property == propertyName &&
                                inValue == false)
                            {
                                inValue = true;
                                inString = true;
                            }
                        }
                        break;
                    default:
                        if (inString)
                        {
                            stringBuilder.Append(c);
                        }

                        break;
                }

                if (property == propertyName &&
                    string.IsNullOrEmpty(value) == false)
                {
                    return ParseToken(typeof(T), value);
                }
            }

            return new JsonToken(JsonTokenType.Null, null);
        }

        private static JsonTokenType GetTokenTypeForPrimitive(Type type)
        {
            if (JSONUtilities.NumericTypes.Contains(type) ||
                JSONUtilities.NumericTypes.Contains(Nullable.GetUnderlyingType(type)))
            {
                return JsonTokenType.Integer;
            }

            if (type == typeof(bool))
            {
                return JsonTokenType.Boolean;
            }

            return JsonTokenType.Null;
        }

        private static string ParseString(string json)
        {
            if (json.Length <= 2)
                return String.Empty;

            StringBuilder parseStringBuilder = new StringBuilder(json.Length);
            for (int i = 1; i < json.Length - 1; ++i)
            {
                if (json[i] == '\\' && i + 1 < json.Length - 1)
                {
                    int j = "\"\\nrtbf/".IndexOf(json[i + 1]);
                    if (j >= 0)
                    {
                        parseStringBuilder.Append("\"\\\n\r\t\b\f/"[j]);
                        ++i;
                        continue;
                    }
                    if (json[i + 1] == 'u' && i + 5 < json.Length - 1)
                    {
                        UInt32 c = 0;
                        if (UInt32.TryParse(json.Substring(i + 2, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out c))
                        {
                            parseStringBuilder.Append((char)c);
                            i += 5;
                            continue;
                        }
                    }
                }
                parseStringBuilder.Append(json[i]);
            }

            return parseStringBuilder.ToString();
        }
    }

    /// <summary>
    /// Represents a Json Token.
    /// </summary>
    public class JsonToken : IEnumerable<JsonToken>
    {
        private object value;
        private IDictionary<string, JsonToken> dictionary;
        private IEnumerable<JsonToken> values;

        /// <summary>
        /// Initializes a new instance of the JsonToken class.
        /// </summary>
        public JsonToken(JsonTokenType type, object value)
        {
            this.Type = type;
            this.value = value;
        }

        /// <summary>
        /// Initializes a new instance of the JsonToken class.
        /// </summary>
        public JsonToken(JsonTokenType type, IEnumerable<JsonToken> values)
        {
            this.Type = type;
            this.values = values;
        }

        /// <summary>
        /// Gets or sets the type.
        /// </summary>
        public JsonTokenType Type
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the enumerator.
        /// </summary>
        public IEnumerator<JsonToken> GetEnumerator()
        {
            return this.values.GetEnumerator();
        }

        /// <summary>
        /// Gets the value.
        /// </summary>
        public T Value<T>()
        {
            try
            {
                return (T)this.value;
            }
            catch
            {
                return default(T);
            }
        }

        /// <summary>
        /// Gets the IEnumerable enumerator.
        /// </summary>
        /// <returns>The collections. IE numerable. get enumerator.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable<JsonToken>)this).GetEnumerator();
        }

        /// <summary>
        /// Gets the children.
        /// </summary>
        public JsonToken[] Children
        {
            get
            {
                return this.values.ToArray();
            }
        }

        /// <summary>
        /// Gets the children count.
        /// </summary>
        public int Count
        {
            get
            {
                return this.values.Count();
            }
        }

        /// <summary>
        /// Gets the dictionary.
        /// </summary>
        public IDictionary<string, JsonToken> Dictionary
        {
            get
            {
                if (this.dictionary == null)
                {
                    this.dictionary = new Dictionary<string, JsonToken>();
                }

                return this.dictionary;
            }
        }

        /// <summary>
        /// Selects the token.
        /// </summary>
        public JsonToken SelectToken(string key)
        {
            if (this.Dictionary.ContainsKey(key))
            {
                return this.Dictionary[key];
            }

            return new JsonToken(JsonTokenType.Null, null);
        }

        /// <summary>
        /// Adds the key to the dictionary.
        /// </summary>
        public void AddKey(string key, JsonToken value)
        {
            this.Dictionary[key] = value;
        }
    }

    /// <summary>
    /// The JsonToken type.
    /// </summary>
    public enum JsonTokenType
    {
        /// <summary>
        /// No type.
        /// </summary>
        None = 0,

        /// <summary>
        /// An array.
        /// </summary>
        Array = 1,

        /// <summary>
        /// An object.
        /// </summary>
        Object = 2,

        /// <summary>
        /// A boolean.
        /// </summary>
        Boolean = 3,

        /// <summary>
        /// A string.
        /// </summary>
        String = 4,

        /// <summary>
        /// Null type.
        /// </summary>
        Null = 5,

        /// <summary>
        /// An integer.
        /// </summary>
        Integer = 6,

        /// <summary>
        /// A float.
        /// </summary>
        Float = 7,

        /// <summary>
        /// A date.
        /// </summary>
        Date = 8,

        /// <summary>
        /// A list.
        /// </summary>
        List = 9,
    }
}
