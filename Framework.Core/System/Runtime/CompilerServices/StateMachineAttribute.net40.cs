#if LESSTHAN_NET45

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Identities the state machine type for this method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    [Serializable]
    public class StateMachineAttribute : Attribute // Note: this class should not be sealed as per Microsoft's design
    {
        /// <summary>
        /// Gets the type that implements the state machine.
        /// </summary>
        public Type StateMachineType { get; }

        /// <summary>
        /// Initializes the attribute.
        /// </summary>
        /// <param name="stateMachineType">The type that implements the state machine.</param>
        public StateMachineAttribute(Type stateMachineType)
        {
            StateMachineType = stateMachineType;
        }
    }
}

#endif