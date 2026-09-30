namespace FC.SDK;

/// <summary>
/// Who a Canon body says it is, from its PTP <c>DeviceInfo</c> (see <see cref="CanonCamera.ReadWpdIdentityAsync"/>).
/// </summary>
/// <param name="Model">The model name, for example <c>Canon EOS 6D</c>.</param>
/// <param name="SerialNumber">
/// The body's DeviceInfo serial number: a stable, per-body id (32 hex digits on an EOS 6D), and NOT the number printed
/// on the body, which is the separate <c>BodyIDEx</c> property (0xD1AF, <see cref="CanonCamera.GetBodyIdAsync"/>) and
/// needs an open session.
/// </param>
public readonly record struct CanonBodyIdentity(string Model, string SerialNumber);
