using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Method)]
    public class ServiceAttribute : Attribute
    {
        public ServiceAttribute(Type scopeType = null) { }
    }
}