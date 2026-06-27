using System.Runtime.CompilerServices;

// Grant the EditMode test assembly access to internal types (EventQueue,
// JsonWriter, ReflectEvent, …) so durability/serialization logic can be unit
// tested directly. The friend assembly only exists in test projects — this has
// no effect on shipped builds.
[assembly: InternalsVisibleTo("Reflect.EditModeTests")]
