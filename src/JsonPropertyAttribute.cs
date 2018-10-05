namespace TinyJson
{
    using System;

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Parameter, AllowMultiple = false)]
    public sealed class JsonPropertyAttribute : Attribute
    {
        /// <summary>
        /// Gets or sets the name of the property.
        /// </summary>
        /// <value>The name of the property.</value>
        public string PropertyName { get; set; }

        public JsonPropertyAttribute()
        {
        }

        public JsonPropertyAttribute(string propertyName)
        {
            PropertyName = propertyName;
        }
    }
}
