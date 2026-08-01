using System.Reflection;
using CrookedToe.Modules.OSCLeash;
using CrookedToe.Modules.OSCAudioReaction;

namespace CrookedToesModules.Tests.OSCLeash;

[TestClass]
public sealed class ModuleLifecycleContractTests
{
    [TestMethod]
    public void ModuleOverridesEveryLifecycleBoundaryThatCanInvalidateInput()
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        MethodInfo? avatarChange = typeof(OSCLeashModule).GetMethod("OnAvatarChange", Flags);
        MethodInfo? playerUpdate = typeof(OSCLeashModule).GetMethod("OnPlayerUpdate", Flags);

        Assert.IsNotNull(avatarChange);
        Assert.IsNotNull(playerUpdate);
        Assert.AreEqual(typeof(OSCLeashModule), avatarChange.DeclaringType);
        Assert.AreEqual(typeof(OSCLeashModule), playerUpdate.DeclaringType);
    }

    [TestMethod]
    public void EachHighFrequencyModuleUsesOneUpdateCadence()
    {
        Assert.AreEqual(1, CountModuleUpdates(typeof(OSCLeashModule)));
        Assert.AreEqual(1, CountModuleUpdates(typeof(OSCAudioReactionModule)));
    }

    private static int CountModuleUpdates(Type moduleType)
    {
        const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        return moduleType
            .GetMethods(Flags)
            .Count(method => method
                .GetCustomAttributes(inherit: false)
                .Any(attribute => attribute.GetType().Name == "ModuleUpdateAttribute"));
    }
}
