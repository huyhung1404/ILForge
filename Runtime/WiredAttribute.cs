using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class WiredAttribute : Attribute
    {
        public Type ScopeType;

        public WiredAttribute(Type scopeType = null)
        {
            ScopeType = scopeType ?? typeof(GlobalScope);
        }
    }
}