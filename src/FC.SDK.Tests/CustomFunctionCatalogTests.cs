using FC.SDK.Canon;
using Shouldly;
using Xunit;

namespace FC.SDK.Tests;

/// <summary>
/// The EOS R6 Custom Function table, pinned on the readings it was built from: every value set on the
/// body with <c>FC.SDK.Diagnostics cfnwalk</c> running (2026-11-17, WPD raw ioctl).
/// </summary>
public class CustomFunctionCatalogTests
{
    private const string R6 = "Canon EOS R6";

    /// <summary>
    /// Every limit set on the body, against the words it sent for them (2026-11-17, and 1/2000 and f/22
    /// on 2026-11-18), and the lens-clamped words of 0x010D (f/16, f/22 and f/2.8 with two lenses).
    /// </summary>
    [Theory]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 9984u, "1/8000")]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 9216u, "1/4000")]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 8448u, "1/2000")]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 0xFFFFF100u, "30\"")]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 0xFFFFF400u, "15\"")]
    [InlineData(CanonCustomFunctionRange.Aperture, 9984u, "f/91")]
    [InlineData(CanonCustomFunctionRange.Aperture, 9216u, "f/64")]
    [InlineData(CanonCustomFunctionRange.Aperture, 6912u, "f/22")]
    [InlineData(CanonCustomFunctionRange.Aperture, 6144u, "f/16")]
    [InlineData(CanonCustomFunctionRange.Aperture, 2304u, "f/2.8")]
    [InlineData(CanonCustomFunctionRange.Aperture, 0u, "f/1.0")]
    [InlineData(CanonCustomFunctionRange.Aperture, 768u, "f/1.4")]
    public void A_range_word_reads_as_the_value_set_on_the_body(CanonCustomFunctionRange range, uint word, string expected) =>
        CanonCustomFunctionCatalog.FormatRangeWord(range, word).ShouldBe(expected);

    /// <summary>
    /// The R6's range menus offer whole stops only, so a word between stops never came from the body
    /// (8960 is what was written for 1/3200) and is not given a name; nor is one past either end.
    /// </summary>
    [Theory]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 8960u)]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 9984u + 128u)]
    [InlineData(CanonCustomFunctionRange.ShutterSpeed, 14u * 768u)]
    [InlineData(CanonCustomFunctionRange.Aperture, 0xFFFFFD00u)]
    [InlineData(CanonCustomFunctionRange.None, 768u)]
    public void A_word_off_the_menu_scale_is_not_named(CanonCustomFunctionRange range, uint word) =>
        CanonCustomFunctionCatalog.FormatRangeWord(range, word).ShouldBeNull();

    [Fact]
    public void A_range_word_off_the_menu_scale_is_shown_raw()
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x010C).ShouldNotBeNull();
        CanonCustomFunctionCatalog.FormatRange(info, [8960, 0xFFFFF400, 8960, 0xFFFFF400])
            .ShouldBe("highest raw 8960 (not a menu value), lowest 15\"");
    }

    [Fact]
    public void The_shutter_range_names_both_limits()
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x010C).ShouldNotBeNull();
        CanonCustomFunctionCatalog.FormatRange(info, [9216, 0xFFFFF400, 9216, 0xFFFFF400])
            .ShouldBe("highest 1/4000, lowest 15\"");
    }

    [Fact]
    public void The_aperture_range_names_both_limits()
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x010D).ShouldNotBeNull();
        CanonCustomFunctionCatalog.FormatRange(info, [9216, 768, 6144, 768])
            .ShouldBe("min. aperture f/64, max. aperture f/1.4");
    }

    /// <summary>
    /// 0x0101 carries whichever increment item was set last: 2 can only be ISO at 1-stop, 1 only the
    /// exposure at 1/2-stop.
    /// </summary>
    [Theory]
    [InlineData(1u, 1, "1/2-stop")]
    [InlineData(2u, 2, "1-stop")]
    public void The_shared_increment_id_names_the_item_its_value_belongs_to(uint value, int item, string option)
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x0101, value).ShouldNotBeNull();
        info.Number.ShouldBe(item);
        info.OptionFor(value).ShouldBe(option);
    }

    /// <summary>
    /// A write of 0x0101 moved the Av/Tv lists while the body kept its increment (#47), so neither item
    /// sharing it is offered for writing; every other R6 item is.
    /// </summary>
    [Fact]
    public void Only_the_shared_increment_id_is_not_writable()
    {
        var table = CanonCustomFunctionCatalog.For(R6);
        table.Where(i => !i.Writable).Select(i => (i.FunctionId, i.Number)).ShouldBe([(0x0101u, 1), (0x0101u, 2)]);
        CanonCustomFunctionCatalog.For("Canon EOS 6D").ShouldAllBe(i => i.Writable);
    }

    /// <summary>0 is 1/3-stop for either increment item, so it cannot say which one was set last.</summary>
    [Fact]
    public void The_shared_increment_id_names_no_item_for_a_value_both_have() =>
        CanonCustomFunctionCatalog.Describe(R6, 0x0101, 0).ShouldBeNull();

    /// <summary>
    /// The four groups an R6 sends, as received with <c>watch</c> on 2026-11-17: one wire group each
    /// (1, 2, 3 and 5), ids in ascending order, every id in the catalog.
    /// </summary>
    [Theory]
    [InlineData("3800000001000000010000002C00000003000000010100000100000002000000080100000100000000000000120100000100000000000000", 1u, new uint[] { 0x0101, 0x0108, 0x0112 })]
    [InlineData("3C0000000100000002000000300000000300000004010000010000000100000005010000010000000100000006010000020000000300000000000000", 2u, new uint[] { 0x0104, 0x0105, 0x0106 })]
    [InlineData("50000000010000000300000044000000030000000C010000040000000027000000F1FFFF0027000000F1FFFF0D0100000400000000240000000000000018000000030000140100000100000001000000", 3u, new uint[] { 0x010C, 0x010D, 0x0114 })]
    [InlineData("6800000001000000050000005C000000070000000607000001000000010000001107000001000000000000001207000001000000000000000E0800000100000000000000130800000100000000000000140800000100000000000000150800000100000000000000", 5u, new uint[] { 0x0706, 0x0711, 0x0712, 0x080E, 0x0813, 0x0814, 0x0815 })]
    public void An_R6_block_parses_into_one_group_in_id_order(string hex, uint group, uint[] ids)
    {
        var block = CanonCustomFunctionBlock.Parse(Convert.FromHexString(hex));
        block.Entries.Select(e => e.FunctionId).ShouldBe(ids);
        block.Entries.ShouldAllBe(e => e.GroupId == group);
        foreach (var entry in block.Entries)
            CanonCustomFunctionCatalog.Describe(R6, entry.FunctionId).ShouldNotBeNull($"0x{entry.FunctionId:X4}");
    }

    [Fact]
    public void An_R6_range_block_carries_four_words_per_range()
    {
        var block = CanonCustomFunctionBlock.Parse(Convert.FromHexString("50000000010000000300000044000000030000000C010000040000000027000000F1FFFF0027000000F1FFFF0D0100000400000000240000000000000018000000030000140100000100000001000000"));
        block.Entries[0].Values.ShouldBe([9984u, 0xFFFFF100u, 9984u, 0xFFFFF100u]);
        block.Entries[1].Values.ShouldBe([9216u, 0u, 6144u, 768u]);
        block.Entries[2].Values.ShouldBe([1u]);
    }

    [Fact]
    public void An_R6_bracketing_block_carries_the_shot_count_in_two_words()
    {
        var block = CanonCustomFunctionBlock.Parse(Convert.FromHexString("3C0000000100000002000000300000000300000004010000010000000100000005010000010000000100000006010000020000000300000000000000"));
        block.Entries[2].Values.ShouldBe([3u, 0u]);
    }

    /// <summary>An R6 sends the number of bracketed shots itself, so 3 is "3 shots", not the fourth option.</summary>
    [Fact]
    public void The_bracketed_shot_count_is_the_wire_value()
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x0106).ShouldNotBeNull();
        info.OptionFor(3).ShouldBe("3 shots");
        info.OptionFor(7).ShouldBe("7 shots");
        info.OptionFor(0).ShouldBeNull();
        info.ValueOf(2).ShouldBe(5u);
    }

    /// <summary>A 6D sends the same count in word 0 (then the menu index in word 1): 3,0 is "3 shots", not "7 shots".</summary>
    [Fact]
    public void A_6D_reads_the_bracketed_shot_count_too()
    {
        var info = CanonCustomFunctionCatalog.Describe("Canon EOS 6D", 0x0106).ShouldNotBeNull();
        info.Word.ShouldBe(0);
        info.OptionFor(3).ShouldBe("3 shots");
        info.OptionFor(2).ShouldBe("2 shots");
    }

    [Fact]
    public void Release_without_a_lens_is_0x0711()
    {
        var info = CanonCustomFunctionCatalog.Describe(R6, 0x0711).ShouldNotBeNull();
        info.MenuLabel.ShouldBe("C.Fn4-4");
        info.OptionFor(1).ShouldBe("Enable");
    }

    /// <summary>Only the increments share an id; every other R6 item has its own, and each option list matches its values.</summary>
    [Fact]
    public void The_R6_table_is_consistent()
    {
        var table = CanonCustomFunctionCatalog.For(R6);
        table.Count.ShouldBe(17);
        table.GroupBy(i => i.FunctionId).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBe([0x0101u]);
        foreach (var info in table)
            if (info.OptionValues is { } values) values.Count.ShouldBe(info.Options.Count, info.MenuLabel);
    }
}
