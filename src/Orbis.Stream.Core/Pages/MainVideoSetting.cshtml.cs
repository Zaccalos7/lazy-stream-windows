using Microsoft.AspNetCore.Mvc;
using Orbis.Stream.Core.Contracts;
using Orbis.Stream.Core.I18n;
using Orbis.Stream.Core.Services;

namespace Orbis.Stream.Core.Pages;

public sealed class MainVideoSettingModel(VideoSettingService videoSettings, Localizer localizer, UiText text) : OrbisPageModel
{
    private static readonly Dictionary<string, string> CustomOnly = new() { ["defaultPlatformConfiguration"] = VideoSettingForm.CustomPlatform };

    public IReadOnlyList<VideoSettingsRequest> Settings { get; private set; } = [];

    /// <summary>The setting in the dialog: <c>?edit=new</c> or <c>?edit={id}</c>, optionally prefilled from <c>?preset=Twitch|Youtube</c>.</summary>
    [BindProperty]
    public VideoSettingForm? Form { get; set; }

    public bool IsNew => Form?.Id is null;

    public void OnGet(string? edit, string? preset)
    {
        // Like the React page after its first poll: the seeded Twitch/Youtube defaults are templates, not listed.
        Settings = videoSettings.GetAllVideoSettings(CustomOnly);

        if (edit is null)
        {
            return;
        }

        int? id = int.TryParse(edit, out var parsed) ? parsed : null;
        Form = id is null ? new VideoSettingForm() : Find(id.Value) is { } setting ? VideoSettingForm.From(setting) : null;

        if (Form is not null && preset is not null)
        {
            var template = videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["defaultPlatformConfiguration"] = preset })
                .FirstOrDefault();
            if (template is null)
            {
                SetNotice(NoticeKind.Error, text["noDataPresent"]);
            }
            else
            {
                Form = VideoSettingForm.From(template);
                Form.Id = id;
                Form.IsActive = false;
            }
        }
    }

    public IActionResult OnPostSave()
    {
        if (Form is null || !Form.IsComplete)
        {
            SetNotice(NoticeKind.Error, localizer.PrintMessage("not.valid.input"));
            return BackToPage();
        }

        var request = Form.ToRequest();
        Run(() => Form.Id is { } id ? videoSettings.EditSettingsVideo(request, id) : videoSettings.SaveSettingsVideo(request));
        return RedirectToPage();
    }

    public IActionResult OnPostToggle(int id)
    {
        if (Find(id) is { } setting)
        {
            Run(() => videoSettings.EditSettingsVideo(
                setting with { IsVideoAndAudioSettingActive = !(setting.IsVideoAndAudioSettingActive ?? false) }, id));
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDuplicate(int id)
    {
        if (Find(id) is { } setting)
        {
            Run(() => videoSettings.SaveSettingsVideo(setting with
            {
                Id = null,
                Title = $"{setting.Title} - {text["copySuffix"]}",
                IsVideoAndAudioSettingActive = false
            }));
        }

        return RedirectToPage();
    }

    public IActionResult OnPostDelete(int id)
    {
        Run(() => videoSettings.DeleteVideoSetting(id));
        return RedirectToPage();
    }

    private VideoSettingsRequest? Find(int id) =>
        videoSettings.GetAllVideoSettings(new Dictionary<string, string> { ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            .FirstOrDefault();
}
