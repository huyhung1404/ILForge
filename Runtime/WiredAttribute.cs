using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public class WiredAttribute : Attribute
    {
        public Type ScopeType { get; private set; }

        public WiredAttribute(Type scopeType = null)
        {
            ScopeType = scopeType ?? typeof(GlobalScope);
        }
    }
}