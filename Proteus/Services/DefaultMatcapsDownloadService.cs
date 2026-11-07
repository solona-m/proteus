using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Proteus.Services.Matcap;

namespace Proteus.Services;

/// <summary>
/// Proteus's own matcap set (pinned release assets, like the starter effects), fetched into the config directory the
/// first time the Studio's Matcaps pane is shown — never at plugin load — and listed from there. Nothing is seeded into
/// the user's own matcap folder.
/// </summary>
public sealed class DefaultMatcapsDownloadService : IMatcapPack, IDisposable
{
    /// <summary>
    /// Immutable by contract, like the effects tag: bump it whenever <see cref="Matcaps"/> changes, and never re-upload
    /// assets under an existing tag, or a mirror keeps serving the old bytes. upload-matcaps.yml publishes it.
    /// </summary>
    internal const string MatcapsTag = "matcaps-v3";

    /// <summary>
    /// Shipped loose (zip bytes are not reproducible, so a pinned checksum would break). Must equal the committed
    /// <c>Proteus/DefaultMatcaps</c> folder byte for byte (<c>DefaultMatcapsDownloadServiceTests</c>).
    /// </summary>
    internal static readonly (string Name, long Bytes, string Sha256)[] Matcaps =
    [
        ("Amber Orange.png", 325396L, "87eeff2ab056bd163fda699a4e51db38b31129f172507792508c0ad33ca1fb6e"),
        ("Amethyst.png", 151935L, "f20e40eceaf99dbd4daa7c3ec6396f69c24285a3dfff4dae8c31e99ea3c07479"),
        ("apple.png", 362170L, "cec04620a42b381568ce8b669ec5517782360deb4a665ccb9e545e29ad054c7e"),
        ("Aquamarine.png", 144219L, "f8b27b7c4111096fbaf987ce5d8b2519eb6f77baf77984b5972bbcdabe6d2b08"),
        ("Basics 1.png", 66831L, "475dcdb37ada71061e417889bd769ce8e94beb74c780bdb71b68fcf95ce58b60"),
        ("Basics 10.png", 29604L, "55e864d1c315ff9d517c03e963a210e5b118e3bf24c9bed8fb56be2ea711b290"),
        ("Basics 11.png", 57636L, "12dbc265cd39447e113d51febb7eb408a358aa0db867557e6c782a9b486fccfb"),
        ("Basics 12.png", 37145L, "c0b243c7924f332ca491346b2832dc17f42952b81458d1ee18be0ea4b7556b26"),
        ("Basics 2.png", 51965L, "45b1770e76dc29f0c21388bc68a5823d1e010a990e18b1a10b6f90c1fde7466a"),
        ("Basics 3.png", 48088L, "a907df39f4bc6c54b108de3f624b0f34a72bf1dde2b3505246f4288821156195"),
        ("Basics 4.png", 36807L, "6c66ac5463fd3aef8a55460de68a3172716faa2c51dc01a8ec3aeea7bc4b43a8"),
        ("Basics 5.png", 42011L, "0b87ef4e3adff5b0a29dfad2677e1b2d4a7a98a588e37c992b24977302df4b33"),
        ("Basics 6.png", 37399L, "7eb3240cd8e6ab9200260fecd8c9fddcfb4e2cd0805fd675f09615647a6aec26"),
        ("Basics 7.png", 67564L, "b469b32da18c9c48dd90373b191cbb8f110ea72f9e8aa4e69b5f28856960b821"),
        ("Basics 8.png", 51573L, "e6b60c82a71673d40572d179e084b37eca0b8dcb4a72ded9342f20a92b1582c7"),
        ("Basics 9.png", 65214L, "752b8eba627785966c6ab9c2cd64187865e92308cc75085efe800986e05fee25"),
        ("Beige Travertine.png", 260873L, "f393a14a98b9581fc526313a32abba0961bae15a35d6d2029277c9fa0d0a0996"),
        ("Blood.png", 58229L, "2948c3f4828907e6c2372ffeaa24967047318583039752f9e7f88343859d9f4a"),
        ("Brushed Steel.png", 136747L, "65e0bea2d87396fd345b5f8c177e1973d04dde7e5ba1013c337b1709c56900ab"),
        ("Citrine.png", 135289L, "0544326f98daa3a5a1a6858ae0bf33f65aac93954b0e67fa01497efac55e239c"),
        ("Copper Metal.png", 286071L, "7409c8d65a1e1a1dd5aa2afd41ef80b3bc772b88d1c26aaaa6f53dd6d553b682"),
        ("copper.png", 39467L, "bd6bb3b9d7e6462d0ba9579e1b5791c53c1d169f5e4231f3b6dbb5ad6be662c6"),
        ("Coral Gloss.png", 148296L, "1e0505f23c01986b8a122c712ac426280bb0214ca766d7c5310fe4891f550a3c"),
        ("Cornflower Blue.png", 198144L, "b6213f0eb8eaac9ed457dcf5d2effc9459560100fbb09423fcc84f41070dde39"),
        ("Cream Speckled Stone.png", 287652L, "59e7d20b1725576cf8c60637e52c033cacefde3ca44f4f760b478d9cea7ae64b"),
        ("Diamond 1.png", 355781L, "3ad1795377cc58c675300b93c7a53a8c19dfa92320a61ddf87378d2c52b37134"),
        ("Diamond 2.png", 311514L, "57fbb9b776e9a5e0cba494484d2d0b10afcbefcbad27d5644a4ca9ffc0435989"),
        ("Diamond 3.png", 193449L, "f1b03997fbe325ea118741b033367c85a7356bc0dc71b8aa54f2fa31c25d0aae"),
        ("Diamond 4.png", 207694L, "b743c908b36c500f909586e5d80d88ab667f32683eaa374392a57216884573ec"),
        ("Diamond 5.png", 242104L, "dd5389763983405a4aeea2ed9f3c30ab3269e2a643e951ebd7bde5fd3055178c"),
        ("Diamond 6.png", 247378L, "d817f8abe040f23bc340d2da1fe5e512fc360fd64325b9933b541f43ee4cd1e2"),
        ("Diamond 7.png", 324520L, "240301bc7d254fc37372f70a7e2cc104f40f9313c4f307a8b4fefd4129922162"),
        ("Diamond 8.png", 262295L, "4c477699021bca26df8bb173f565e20dcf37c20e625c163c8eed9d892a7a1edb"),
        ("Diamond 9.png", 328944L, "2958b5ed4dc70e16faba3fde327d2af1bbbd7a9fae748b1c7f7998ef2d377fd0"),
        ("Diamond.png", 175320L, "7a3499904e214d33df198ba20f9cb46a6839ea7db3471a7632bf174e719b4e21"),
        ("embossed.png", 489673L, "51b278e1f17923508a43d82fd8473f16f7ad814adbe9245112310472373a1ede"),
        ("Emerald.png", 101740L, "4f63d8a5afe681f7b883963ca1611c3c820bc6065a833620727b2ea83b63c251"),
        ("Gem 1.png", 133194L, "7fdb19bedc8a1d4137213382f355b7089abd324eb4bec9c54e6137573f128878"),
        ("Gem 2.png", 195298L, "a7a8705da756465d07b359e76242eb0b658ee1311f76f4785e0a3848c8683cbb"),
        ("Gem 3.png", 282637L, "645271903ba006366904120aeea133169aa8fa86f61ac90b48bcff1f938d3121"),
        ("Gem 4.png", 286817L, "eaa6524674d1a75ba322586bb469c36a9d7c852d8d50adea6103267f204b75e9"),
        ("Gem 5.png", 298035L, "7b119eb4d58addec0c2234784bf12e538837446277b57027cf96023074f1311c"),
        ("Glass 1.png", 58495L, "02aaa475d9e7888e0a11968be9185987bd8cf965b778bc1cecf591b7ac2d42ec"),
        ("Glass 10.png", 146184L, "fd7007c1ea0a005ca4ea63b7fa4a1190fcf63c18f7d9f8385b0cc9c27958c302"),
        ("Glass 2.png", 113336L, "2db10d136290ff8e6863cab1cebe411ea0136d66a398d8edb89f348172290e62"),
        ("Glass 3.png", 140454L, "326d9c3dde10992d413c1650c74d4d94075e678d8591e5101594813da18241f8"),
        ("Glass 4.png", 122588L, "a728a35625afd9a77615eae31099319442c6bba229a41d6721ef9b04a3f0a3e3"),
        ("Glass 5.png", 111586L, "8456d3b65f1eb8a4246b075720ab7afb842f9d541378ce8ebc8cde27297fc34e"),
        ("Glass 6.png", 184312L, "7a0b86d081bfa7b1db33be4013e03030ca96830bdfea60d2f56801f57b74d286"),
        ("Glass 7.png", 154989L, "25f230dd0eec956d9f0d3ab8512072212c3aa53af27d1a84ea3aefce60d17453"),
        ("Glass 8.png", 159341L, "2d1861c32e5af79d0f018bb570c204c032129622319d12d71c0acf2a7ce2d9cc"),
        ("Glass 9.png", 170299L, "abae6297de193e641a2800640e057c66c06ad1bc8a2ac78abbe3dddac5933707"),
        ("Gold 1.png", 301607L, "209ff6e4613a83afd19db4b99a02bc5c8bcfdf0400ce0f0fb036e83657d9f3a0"),
        ("Gold 2.png", 267396L, "0644f052664726008677404d81f6ed44ee7004bf9089423db56bfcfbd74aeb5f"),
        ("Gold 3.png", 257029L, "48dfa960fe38a533b6cea87d654b152b0530c90ec542f616e0a7bb1e8adacc29"),
        ("Gold 4.png", 270131L, "7732454cfd229c7fa4910b68f0d3ce3d6f3ef9101e07de4b9f15a147cff14c30"),
        ("Gold Car Paint.png", 264461L, "acb9ff6e0ce49d20c4549b09c2eef948c5da0646401edb75dc72e84659b927cc"),
        ("Gold Metal.png", 206616L, "d8bf4a3bdf237b97522daf5da1a872417f26bc793534a2643098e38c169f88ef"),
        ("Grey Glass Car Paint.png", 107547L, "f678aa9f40283600d76500f9dd3cedbdb57d611cc4e446c9b8f3ce06a526cf5e"),
        ("Ice Blue Marble.png", 185749L, "35152f02ae2d7c8c3ca4af2ec4779a4a25de9a95009af43a55d56d0b19836510"),
        ("Iridescent 1.png", 111634L, "46f0dc4fd6100ba4e6cafbd028201b74f8479a06f8eab156e9206221ebbb72f8"),
        ("Iridescent 10.png", 242462L, "43434b03f366cadabdd53ddb8998743c98f6861fc178c032b2155830ca3d023b"),
        ("Iridescent 11.png", 240126L, "36542896583a16960891c95d80692dfc1b7083d9abb02ed335bdd101273ef656"),
        ("Iridescent 12.png", 305075L, "7d5241582184bb1cd5abcb633a8835bc3f323fedd2e91fcbea7ae7f337fc4807"),
        ("Iridescent 13.png", 320211L, "2409c4f176a55a4c2e6c56045a174575fe66f970e70a5e155fce6f5e85da537e"),
        ("Iridescent 14.png", 114106L, "5e18cb10b947ab68707e299de79f11b09f119075c803ae4b5f7ee6f9ff9834ff"),
        ("Iridescent 2.png", 117994L, "db05d33079107ade72eeb5e6da09ca16589255c764e9663e6a1fe59b1b93849f"),
        ("Iridescent 3.png", 167664L, "d983de5873678dfc60c07d8f944f2e5968fa6351afff1ef72afc1c24d05652c1"),
        ("Iridescent 4.png", 269599L, "3d8a915cc4b6905ebdfa7a014f1cd98a3e969ac359de0af79201039ec6f666b0"),
        ("Iridescent 5.png", 262741L, "dabe311801d1a6a1151b3701b4a2184f0c5eecce465306b3883632360efa8ca6"),
        ("Iridescent 6.png", 193828L, "3953c78ec9ca1856316e6be3c2fc373fac12550b5ee23e9bf9001f0f83496cf5"),
        ("Iridescent 7.png", 127608L, "3beb1af49b7e18a45eb0265ca5690d5cd783a522c62fd1423cabb038f4a9806a"),
        ("Iridescent 8.png", 218703L, "8fd5ffffbcf4bd4d5cf7139f45f09228e34be2de1cc16db77edff9500fc204ed"),
        ("Iridescent 9.png", 146858L, "0b8e79ea735baf54d75b96db8ee2b28135dd20541c4d131954a023dff436031d"),
        ("Light Leak 17.png", 214550L, "99069232641639d172569bc045b498c7eae3403add278a083997ca3a5660cf8b"),
        ("Light Leak 3.png", 230788L, "b55dfc2d85cff04b3b4a62a4ae31af1e814a7cf9644235094273ad01494c3574"),
        ("Light Leak 30.png", 200043L, "280dc3bd9deb7bc30439966116c425d01ac7d93b7da6f066a5277b7f3a20f1fd"),
        ("Light Leak 36.png", 218318L, "589662caec03142c17ed015781472b5486725b207ad88f6e2ab3bcf707f8c343"),
        ("Light Leak 39.png", 207765L, "129a83a9bceab13289fafe467ab385bcf5c87b5bdf0b0a0a023d51ff3b62d2c6"),
        ("Light Leak 44.png", 221685L, "80893dfafd26be8414a25faa74358649c639dbff33674eabd6b2d5a88bb3022a"),
        ("Light Leak 5.png", 216361L, "cabfbf36cb2db39d3c113955eac12e9a2dbe8f58ca22f4c6a9bd53f74073048a"),
        ("Light Leak 8.png", 202217L, "4bb800e07894039f16365329bb2c2b3e7b024084de75b1595f0a6750985cf8e6"),
        ("Lime Green Satin.png", 267167L, "41cf61382fcc144104f1e839f7b4be7573824024ef363a7eca095e7ab0ac44dc"),
        ("matte red.png", 225400L, "769973cb8239f951252a11cfcc8773118c9d6d9b1646982d3eb1a40ec7d9e4cb"),
        ("Metal 1.png", 71384L, "7fe7d3024edab8db2b8e77427b76ead6e3a24fbecad080eff0ccfca53e5933d7"),
        ("Metal 2.png", 80051L, "8f95a2bd27d4a28a2e2cd96338f544cd2937d54f0315e364cb053d6a4081da0f"),
        ("Metal 3.png", 88651L, "8a17ccab623ab9883bad4d325a4ab186fddefe733f4a917095a45c2af82eb5eb"),
        ("Metal 4.png", 98474L, "723906ee71b9a743e5bb35831616906cab4e5f54b1f65ab4924066f85ded4253"),
        ("Metal 5.png", 246867L, "9165ede1729e960d7fa103992be323329db7405df2abb6e166b1c964a2d15b0e"),
        ("Metal 6.png", 91427L, "0d93d5ea2575ba064c6bed08cad423ca3bca96f6d8010330e26e9c6ab9aae420"),
        ("motherofpearl.png", 303724L, "ac7df66db155e07ddf92dfe01589d94bde6fa18d243d2448e829470a65bc02ab"),
        ("NeonPink.png", 310349L, "7a362262e8c7fdb9a42efd4148a784922b9575ed82758dc24e113f8815a228d5"),
        ("Peach Satin.png", 336647L, "4f3120f333f83cb2b4607673f142c387596de3efc9b8094637b720c180f4577f"),
        ("Pearl 1.png", 163991L, "f5508ecb7bafb6ce015032bf37d76f09addae136b01bd4241f6ae33211893ad4"),
        ("Pearl 2.png", 192501L, "731f87547dfa59da4f48f262bc14722c33f3a5ce0b969c0384bf2eaa18a7661a"),
        ("Pearl 3.png", 165650L, "23235649864d8f1fdabb6305a375df4cec95def771dc18d689d9ca45552779ec"),
        ("Pearl 4.png", 162644L, "50bda2e7279d0d9a1f34efa14c39fdf4e236e32059ca9c5af80b8923021c62be"),
        ("Pearl 5.png", 144328L, "dc7369e337bb1472e8e462f6b0fc7df6ff4b78aa42f389bae99349d80c070373"),
        ("Pearl 6.png", 119700L, "657683e711839a75cb7fd7d6bf44d50c0ce55fe9f5048ff954a751848b4bb7ef"),
        ("Pearl 7.png", 140705L, "13d8ddadc083ff927c41edd039e6976c6385e14ec0cdc8c50756fe5ed13cdfa2"),
        ("Pearl White.png", 125388L, "7f229a849aa2f2da5038a5b13492e65f03386177540e681614b8b7549a43aaf3"),
        ("Pink Pearl.png", 126282L, "5450b2325fa1ce7356bccab11470dfa047970dc2fecf143aaf9cca80eb17e8e4"),
        ("Pink Topaz.png", 136693L, "8b562c44ebadee588da60dcdadde85423f33677b880e7194d9d670efec32c868"),
        ("Rainbow Flecks.png", 211560L, "b59454852b2c8b631419ff3840b6ffe5746480fd8a846cad93a8d8095f11faad"),
        ("rainbow.png", 273721L, "45fbbe3f0ea4b2ff1733b2a6076fb22a498f52bee38c92e37d3cd101bdf41bdd"),
        ("redgold.png", 496717L, "5dd6bd118d20e7a2de107a29e7792291a952575ca73f4191b93d95ca1e1dae9b"),
        ("Rubber 1.png", 88672L, "5510ac745a751cab79c641a51f7d8dec3b3a3f9434d7204c4180e88a4109596b"),
        ("Rubber 10.png", 58836L, "aa5eb98cda0b6e8bee62bb13d5a2ac12be10ce9a89fd5496d20f98f93b198798"),
        ("Rubber 11.png", 52265L, "059308ec3415715a63a4689137e47787ed6277b76f624333b95660cffaeb1991"),
        ("Rubber 2.png", 89255L, "91955045b6d272fd0a3cbb5c9db2e4a739047928eddf05f939591901c5593a73"),
        ("Rubber 3.png", 82662L, "ea2cb0b0b09a2c97f822256bd9df4bb9a4a84115e04a7466b13934a6112f1d99"),
        ("Rubber 4.png", 88193L, "ae4f6841e644b60a0f45be3b6845c6b42c69b6c54729eb6a99c589edd09f32c0"),
        ("Rubber 5.png", 76727L, "47f1514fa74acee4446eca0ad9900d76508cc0b3ef8d420c2ad1bcb8300c1746"),
        ("Rubber 6.png", 78850L, "cc9c3f3466b3e0f4f744c3125be953b3512a63740656fec2839db2cb3d0ec34e"),
        ("Rubber 7.png", 62568L, "b7cba8150c614842cdfe65fc8754a9afceddfbac0982417bc56e81342797717a"),
        ("Rubber 8.png", 56680L, "9abda15a1d7f867c826f64f20a96071d9d9a97256bc043a0f4a61d1bcb1086bc"),
        ("Rubber 9.png", 59862L, "c397b4e318a5ec3435f69136c880819c007fa367f545453b3c322668afdb39b8"),
        ("Ruby.png", 122484L, "8defa801e2c120d21d2fcd858ec2d07b7895b9930bbcbbd885c7d764c8cd9bb9"),
        ("Saffron Yellow.png", 284055L, "7211bc3d8e35ae5dd9db56a62bd1dca0d38df1904bf94acaad0d2ac6b623a54f"),
        ("Sage Grey Car Paint.png", 95895L, "e85f3c6b6e089d76718b7faa3905f6b78f3ea3dc09afbea1107091a3e41ec621"),
        ("Sand Satin.png", 269564L, "680868d96b209f2fa7e33d37f5912a348c2ae0efcab78505057c5aa96e19eb9a"),
        ("Sapphire.png", 138270L, "9ad85145192423bc160380e81b7a35c614c8f2bf93316233c6e5c7acbf2b7375"),
        ("Shine 1.png", 51386L, "c5d0c3677a872033b2009382e2a4c6b8daf0748755b6294ba4dcdb15919c419a"),
        ("Shine 10.png", 108873L, "d35240e35274c2f715f43c62da2c998ba7eaa58f3750e3c714d29195540b072a"),
        ("Shine 2.png", 25468L, "7f90fa32fe2083aa5a8d84e3ce5c3d222f6f1d7f3434790c75b66ae20c5df099"),
        ("Shine 3.png", 22257L, "11ea09332e3880796b8310cca098184ef037823c13ef64278751ddbd317e27e6"),
        ("Shine 4.png", 115860L, "90ec0edeaf3454dce54dc199bb925e1e0a39146b549a1c2ec1bb998c1affb2c9"),
        ("Shine 5.png", 108001L, "5aae632c41ff77e09de61f98f9adb4f080528c2b212c15bd01ad5c0111bdb86b"),
        ("Shine 6.png", 101723L, "73a0c8fad7eb8e45aa5b1e732854aae790545b1555f01ce07bab27d11a5f9b2c"),
        ("Shine 7.png", 54340L, "278bfc42ed4a56b4e3b61298e1b4a5750d088b13c9aa7c9e4a9d007c253edf36"),
        ("Shine 8.png", 31359L, "9ecb348c43375622bd5066ca53c09c1fb2e896ee791eb37a96ab44909ee070c6"),
        ("Shine 9.png", 37299L, "31475ea88a877cfcfb5e324f41ca8c83925edcaad86f6b28f54bddd8ca484946"),
        ("Silk A Blue.png", 162433L, "14c029bb28753ee736c60e9769fdd19288bf137eb53c07343279cf7bd1e1ae73"),
        ("Silk A Pearl.png", 162063L, "7074b96948b820028f2edbbb792681ae5fa96bdf30eeaaeb84a2e0b70aa6317a"),
        ("Silk A Pink.png", 161056L, "85f8f78486ea43dd93e434d518a9d45a0b6585ec2c9577b08ca80a9adf23659a"),
        ("Silk A White.png", 155697L, "f61c317e367736600c6dbb4e156c7babbc2337fed64e807a7f59a5379258d92b"),
        ("Silk B Black 2.png", 69086L, "f084edbb48a22c20d18a79a85e1b122b59908cc80e8bd3f6bbc30a0136032706"),
        ("Silk B Black.png", 123519L, "762601ffcbe3388062b16309e66e762f4168fea27ca1f4cad616eaa84578d83e"),
        ("Silver Satin.png", 201668L, "941b6c669a4e1726c7ac4e0ffb036d47b3b06362a9e4a692af407d5fd461f395"),
        ("Slate Blue Grey.png", 240744L, "c2444bf12c4b6391dac32e78bb5310241d93ab73a9dda07b8321c54cafe4196d"),
        ("Teal Blue Gloss.png", 414255L, "813a1be5535856334b5b661f9ff8f536e91eb572ebb68d78095d5295821230f8"),
        ("Tomato Red.png", 304945L, "ca500efdb536181a54a0f0848c8bf3d8ff6d8896ac3dfdbb2954d04ccc061447"),
        ("Turquoise Satin.png", 205224L, "1172473ea62842d6bbf7a98f3518dce5d86e4d17f7c13d990b21e036101217fb"),
        ("Violet.png", 225188L, "66fdbc11c7e88fb3edd0b6c88a554cce9c6299901b41e716e5716f24eaea8bee"),
        ("White Marble.png", 99579L, "b2d143ed054136df7207aa642a91672f2d53d782a00169c5fe0578e774f7d7bf"),
    ];

    /// <summary>The asset name to ask for: GitHub rewrites spaces, and upload-matcaps.yml uploads with this substitution.</summary>
    internal static string RemoteName(string localName) => localName.Replace(' ', '.');

    /// <summary>Fetches one file: the seam tests replace; <see cref="ResilientDownloader.FetchAsync"/> otherwise.</summary>
    internal delegate Task<FetchResult> Fetch(string[] baseUrls, string fileName, long expectedBytes, string expectedSha,
                                              string destPath, Action<long>? onProgress, CancellationToken ct);

    private readonly IPluginLog log;
    private readonly ResilientDownloader? downloader;
    private readonly Fetch fetch;
    private readonly string dir;
    private readonly CancellationTokenSource cts = new();
    private readonly object gate = new();
    private MatcapPackStatus status;
    private bool running;
    private bool started;
    private long version;

    /// <summary>Where the matcaps are kept: <c>&lt;config&gt;\DefaultMatcaps</c>.</summary>
    public string Directory => dir;

    public DefaultMatcapsDownloadService(IPluginLog log, string dataDir) : this(log, dataDir, null) { }

    internal DefaultMatcapsDownloadService(IPluginLog log, string dataDir, Fetch? fetch)
    {
        this.log = log;
        if (fetch == null) downloader = new ResilientDownloader(log);
        this.fetch = fetch ?? downloader!.FetchAsync;
        dir = Path.Combine(dataDir, "DefaultMatcaps");
        status = new MatcapPackStatus(MatcapPackState.Idle, 0, Matcaps.Length, "");
    }

    public MatcapPackStatus Status { get { lock (gate) return status; } }

    public long Version => Interlocked.Read(ref version);

    /// <summary>A run is under way (tests: the final state is never published while one still is).</summary>
    internal bool IsRunActive { get { lock (gate) return running; } }

    /// <summary>The state and whether a run is active, read together (tests: a run is active exactly while Downloading).</summary>
    internal (MatcapPackState State, bool Running) Snapshot() { lock (gate) return (status.State, running); }

    public IReadOnlyList<MatcapImage> Available()
    {
        var list = new List<MatcapImage>();
        foreach (var (name, bytes, _) in Matcaps)
        {
            var p = Path.Combine(dir, name);
            if (AtPinnedSize(p, bytes)) list.Add(new MatcapImage(name, p, false, FromPack: true));
        }
        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void EnsureAsync(bool retryFailed = false, bool evenIfReady = false)
    {
        lock (gate)
        {
            if (running) return;
            bool rearm = (retryFailed && status.State == MatcapPackState.Failed)
                         || (evenIfReady && status.State is MatcapPackState.Ready or MatcapPackState.Failed);
            if (started && !rearm) return;
            started = true;
            running = true;
            status = status with { State = MatcapPackState.Downloading, Detail = "", Have = CountHave() };
            Interlocked.Increment(ref version);
        }
        Task.Run(Run);
    }

    private async Task Run()
    {
        var end = MatcapPackState.Ready;
        string detail = "";
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            SetHave();
            foreach (var (name, bytes, sha) in Matcaps)
            {
                cts.Token.ThrowIfCancellationRequested();
                var dest = Path.Combine(dir, name);
                if (AtPinnedSize(dest, bytes)) continue;

                var r = await fetch(ProteusAssets.BaseUrls(MatcapsTag), RemoteName(name), bytes, sha, dest, null, cts.Token)
                    .ConfigureAwait(false);
                if (r.Ok)
                {
                    SetHave();
                    Interlocked.Increment(ref version);
                    continue;
                }
                end = MatcapPackState.Failed;
                detail = $"{name}: {r.Detail}";
                log.Warning("[Proteus] Matcap {0} unavailable ({1})", name, r.Detail);
                // Only an asset that is not there (404/410) is skipped; anything else — the network, a throttled or
                // failing origin, a checksum that never matched — stops the run, which Try again resumes.
                bool absent = r.Failure == FetchFailure.Http && r.StatusCode is 404 or 410;
                if (!absent) break;
            }
            if (end == MatcapPackState.Ready) log.Information("[Proteus] Proteus's matcaps are ready.");
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            _ = ex;   // plugin unloading: not a failure
            end = MatcapPackState.Idle;
        }
        catch (Exception ex)
        {
            end = MatcapPackState.Failed;
            detail = ex.Message;
            log.Warning(ex, "[Proteus] Downloading Proteus's matcaps failed");
        }
        finally
        {
            int have = CountHave();
            lock (gate)
            {
                running = false;
                status = status with { State = end, Detail = detail, Have = have };
                Interlocked.Increment(ref version);
            }
        }
    }

    private void SetHave()
    {
        int have = CountHave();
        lock (gate) status = status with { Have = have };
    }

    private int CountHave() => Matcaps.Count(m => AtPinnedSize(Path.Combine(dir, m.Name), m.Bytes));

    /// <summary>Present means at the pinned size: a truncated or other-revision file is fetched again (and not listed).</summary>
    private static bool AtPinnedSize(string path, long bytes)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == bytes; }
        catch { return false; }
    }

    /// <summary>Cancels any in-flight fetch and releases the HTTP client, which load-context unloadability requires.</summary>
    public void Dispose()
    {
        cts.Cancel();
        downloader?.Dispose();
    }
}
