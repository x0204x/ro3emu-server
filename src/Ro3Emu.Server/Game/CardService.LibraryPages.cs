using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CardService
{
    internal const int GainLibraryPageAction = 207;

    /// <summary>
    /// 207 GainLibraryPage [207, page]: the chapter items 1007011..1007102 ("Use to unlock Chapter 1 of a book") add
    /// LibraryPageConfig page <c>page</c> (the row's _kPageItem names the item back) to 5057 pages as {pageId, Gain}
    /// (LibraryPageState None 0, Read 1, Gain 2; the client's enum_PageStatue NoRead, Read, Get).
    /// The Codex book panel counts a page owned when its state is not NoRead (LC_Library_Helper.GetPageStatue,
    /// UI_Sys_Library_ChapterPanelCtrl); claiming the book's reward turns its pages to Read. The client checks nothing
    /// before 31323 and the page arrives through the 5057 pages diff (LC_DAL_Library._onSyncUpdatePageInfo).
    /// The books are placeholder content in this client (LibraryBook, function 6003, level 999); seven page
    /// items name pages without a row (10062, 10081..10102) and are ItemNoConfig, keeping the item. (inferred) A page
    /// already held is AlreadyUnlocked, keeping the item; its _kPageAttribute is not applied (no reader of it here).
    /// </summary>
    private async Task<int> UseLibraryPageItemAsync(ItemActionUse use)
    {
        var s = use.Session;
        if (s.Library == null) return ErrorCodes.FeatureUnavailable;
        if (use.Count != 1) return ErrorCodes.Parameter;
        var pages = use.Effects.Select(e => e.Length > 1 ? (int)e[1] : 0).Distinct().ToList();
        if (pages.Any(page => _data.Row("LibraryPageConfig", page) is not { } row || GameData.Int(row, "_kPageItem") == 0))
            return ErrorCodes.ItemNoConfig;
        if (pages.All(page => s.Library.Pages.TryGetValue(page, out var held) && held.State != LibraryPageState.None)) return ErrorCodes.AlreadyUnlocked;
        var next = s.Library.Clone();
        foreach (int page in pages.Where(page => !(next.Pages.TryGetValue(page, out var held) && held.State != LibraryPageState.None)))
            next.Pages[page] = new LibraryPageInfo { PageId = page, State = LibraryPageState.Gain };
        byte[] saved = next.ToByteArray();
        if (!await _character.CommitItemActionAsync(use, tx => tx.RoleBlob(s.Role!.Id, "library", saved), () => s.Library = next,
            new { Pages = pages })) return ErrorCodes.ItemsShort;
        await CharacterService.SyncAsync(s, LibrarySync, s.Library.Clone());
        return 0;
    }
}
