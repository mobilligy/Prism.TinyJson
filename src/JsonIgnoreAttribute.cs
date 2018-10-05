namespace TinyJson
{
    using System;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class JsonIgnoreAttribute : Attribute
    {
        public JsonIgnoreAttribute()
        {
        }
    }
}
