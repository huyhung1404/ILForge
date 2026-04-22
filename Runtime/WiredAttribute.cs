using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class WiredAttribute : Attribute
    {
        public WiredAttribute(Type scopeType = null) { }
    }
}