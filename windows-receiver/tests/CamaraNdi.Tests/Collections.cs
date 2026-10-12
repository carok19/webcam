using Xunit;

namespace CamaraNdi.Tests;

/// <summary>
/// Las pruebas de punta a punta (codifican, transmiten y decodifican video de verdad) y la de
/// rendimiento usan mucho CPU: se corren una por una para que no se estorben entre sí.
/// </summary>
[CollectionDefinition("E2E", DisableParallelization = true)]
public sealed class E2ECollection { }
