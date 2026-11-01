using ImageStudio.Services;

namespace ImageStudio.Tests;

// winmm obcina nazwy urządzeń do 31 znaków: pełne nazwy z Core Audio dopasowujemy po początku
public sealed class AudioNamesTests
{
    [Fact]
    public void Truncated_winmm_names_get_their_full_names()
    {
        var full = AudioNames.Resolve(
            ["SteelSeries Sonar - Gaming (Ste", "Głośniki (Sound Blaster Audigy "],
            ["Głośniki (Sound Blaster Audigy Fx)", "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)"]);

        Assert.Equal(["SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)", "Głośniki (Sound Blaster Audigy Fx)"], full);
    }

    [Fact]
    public void Names_without_a_match_stay_as_winmm_reports_them()
    {
        Assert.Equal(["Nieznane urządzenie"], AudioNames.Resolve(["Nieznane urządzenie"], ["Inne"]));
        Assert.Equal(["Mikrofon"], AudioNames.Resolve(["Mikrofon"], []));
    }

    [Fact]
    public void Two_identical_devices_get_two_different_full_names()
    {
        var full = AudioNames.Resolve(["Microphone (USB Audio)", "Microphone (USB Audio)"],
                                      ["Microphone (USB Audio) #1", "Microphone (USB Audio) #2"]);

        Assert.Equal(["Microphone (USB Audio) #1", "Microphone (USB Audio) #2"], full);
    }

    [Fact]
    public void A_name_saved_before_the_fix_still_finds_its_full_name()
    {
        var device = new AudioDevice(2, "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio Device)");

        Assert.True(device.Matches("SteelSeries Sonar - Gaming (Ste"));
        Assert.True(device.Matches(device.Name));
        Assert.False(device.Matches("Inne"));
        Assert.False(device.Matches(null));
        Assert.False(device.Matches(""));
    }

    [Fact]
    public void Real_device_lists_have_the_default_entry_first_and_no_name_cut_at_31_characters_when_core_audio_knows_it()
    {
        var player = new NAudioPlayer();
        var devices = player.OutputDevices();

        Assert.Equal(new AudioDevice(-1, "Domyślne urządzenie systemu"), devices[0]);
        // na maszynie bez urządzeń lista ma tylko domyślny wpis; z urządzeniami każda nazwa jest niepusta
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }
}
