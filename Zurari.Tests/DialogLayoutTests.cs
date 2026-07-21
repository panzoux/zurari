using Xunit;

public class DialogLayoutTests
{
    // レイアウト: 内容(幅・高さ)と画面サイズから中央配置の矩形を算出する。
    // 戻り値は (X, Y, Width, Height)。Width/Height は枠線2文字分を含む外形サイズ。

    [Fact]
    public void CalcDialogRect_CentersSmallContentInLargeScreen()
    {
        var rect = DialogLayout.CalcDialogRect(contentWidth: 20, contentHeight: 3, screenW: 100, screenH: 40);

        // 外形 = 内容 + 枠線2(左右/上下それぞれ1)
        Assert.Equal(22, rect.Width);
        Assert.Equal(5, rect.Height);
        // 中央配置: X = (100 - 22) / 2 = 39, Y = (40 - 5) / 2 = 17
        Assert.Equal(39, rect.X);
        Assert.Equal(17, rect.Y);
    }

    [Fact]
    public void CalcDialogRect_OddRemainderLeansTopLeft()
    {
        // 画面99x39、外形22x5 のとき余り(99-22=77, 39-5=34)は偶数なので中央ぴったり。
        // 余りが奇数になるケースで切り捨て(小さい方)がX/Yどちらに寄るかを固定する。
        var rect = DialogLayout.CalcDialogRect(contentWidth: 19, contentHeight: 3, screenW: 100, screenH: 40);
        // 外形 = 21x5。余り X: 100-21=79 (奇数) → 79/2=39 (切り捨て)。余り Y: 40-5=35(奇数)→35/2=17。
        Assert.Equal(39, rect.X);
        Assert.Equal(17, rect.Y);
    }

    [Fact]
    public void CalcDialogRect_ClampsWidthToScreenWidthMinusMargin()
    {
        // 内容が画面より大きい場合、外形は画面幅から左右マージン2文字を引いた値でクランプする。
        var rect = DialogLayout.CalcDialogRect(contentWidth: 200, contentHeight: 3, screenW: 40, screenH: 20);

        Assert.Equal(38, rect.Width);   // 40 - 2(マージン)
        Assert.Equal(1, rect.X);        // 中央寄せの結果、左マージン1
    }

    [Fact]
    public void CalcDialogRect_ClampsHeightToScreenHeightMinusMargin()
    {
        var rect = DialogLayout.CalcDialogRect(contentWidth: 10, contentHeight: 100, screenW: 60, screenH: 20);

        Assert.Equal(18, rect.Height);  // 20 - 2(マージン、上下フッター2行を考慮しない単純ケース)
        Assert.Equal(1, rect.Y);
    }

    [Fact]
    public void CalcDialogRect_MinimumSizeIsRespectedEvenForTinyContent()
    {
        // 内容が極小(幅0高さ0)でもタイトル枠が成立する最小外形(幅4×高さ3)を下回らない。
        var rect = DialogLayout.CalcDialogRect(contentWidth: 0, contentHeight: 0, screenW: 100, screenH: 40);

        Assert.True(rect.Width >= 4);
        Assert.True(rect.Height >= 3);
    }

    [Fact]
    public void CalcDialogRect_NeverExceedsScreenBounds()
    {
        var rect = DialogLayout.CalcDialogRect(contentWidth: 500, contentHeight: 500, screenW: 30, screenH: 15);

        Assert.True(rect.X >= 0);
        Assert.True(rect.Y >= 0);
        Assert.True(rect.X + rect.Width <= 30);
        Assert.True(rect.Y + rect.Height <= 15);
    }
}
