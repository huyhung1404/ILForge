using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Method)]
    public class ServiceAttribute : Attribute
    {
        public Type ScopeType;

        public ServiceAttribute(Type scopeType = null)
        {
            ScopeType = scopeType ?? typeof(GlobalScope);
        }
    }
}