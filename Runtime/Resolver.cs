namespace ILForge
{
    public static class Resolver
    {
        public static T Get<T>() => default;
        public static T Get<TScope, T>() where TScope : Scope => default;
    }
}
