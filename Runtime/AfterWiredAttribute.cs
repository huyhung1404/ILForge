using System;

namespace ILForge
{
    [AttributeUsage(AttributeTargets.Method)]
    public class AfterWiredAttribute : Attribute
    {
        public int Order;

        public AfterWiredAttribute(int order = 0)
        {
            Order = order;
        }
    }
}