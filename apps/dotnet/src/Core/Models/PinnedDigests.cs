// Copyright 2026 Shazron Abdullah and Bunyi contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Bunyi.Core.Models;

/// <summary>
/// SHA-256 digests for the files of the default Hugging Face repositories, and for Whisper.
/// </summary>
/// <remarks>
/// <para>
/// A download from a Hub repository is a fetch from <c>resolve/main</c>, a moving branch, so
/// without a digest whatever the repository holds today is loaded into native parsers. These are
/// the digests the project mirror publishes in <c>manifest.sha256</c> at
/// <c>models.bunyi.app/onnx/*</c>, and every one was checked against the repository itself (the
/// LFS object id for large files, a download for the four small ones per repository), so a
/// download from the Hub is verified exactly as one from the mirror is.
/// </para>
/// <para>
/// They apply only to the repositories named here. A user who points a mode at another repo id
/// gets no pinning, because a different export has different files.
/// </para>
/// <para>
/// When a repository is legitimately updated, the new digests belong here and in the mirror's
/// manifest together; until they are, a download of the changed file fails verification
/// rather than installing it.
/// </para>
/// </remarks>
internal static class PinnedDigests
{
    /// <summary>The digests for a repository's files, keyed by relative path, or null if it is not pinned.</summary>
    public static IReadOnlyDictionary<string, string>? ForRepo(string repoId) =>
        ByRepo.TryGetValue(repoId, out var digests) ? digests : null;

    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> ByRepo =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["elbruno/Qwen3-TTS-12Hz-0.6B-CustomVoice-ONNX"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code_predictor.onnx"] = "4e741d1e16ba61ca446b060b16d2da9519b11d18aae5d7bbbfcd273745a38225",
                ["embeddings/config.json"] = "40623c05e03cf97cb5dc8d2ba84aeb0c8f5649600c91bb56d65b416a804f2f56",
                ["embeddings/cp_codec_embedding_0.npy"] = "9b1bd89a06be7fc6eb07e6b03d6d3fdc4762b5d0e0db124d74c608c0638082e3",
                ["embeddings/cp_codec_embedding_1.npy"] = "f0d6e187dec6f037980e38911b27016c0531bb34718bb96ccb954366fc495d21",
                ["embeddings/cp_codec_embedding_10.npy"] = "0e6d1bd968d39d53064bf430a0117cd83d452ee3ecf78dbe2648ad4b783d9091",
                ["embeddings/cp_codec_embedding_11.npy"] = "c9a1cb9ed2749c9f94fc29a939afdbb8ec341ba3f734c10b507f82a441a7c20e",
                ["embeddings/cp_codec_embedding_12.npy"] = "4cc774bda9a3b868461a36d08cd92370b71e162589598d1796b66fd1bb0520d1",
                ["embeddings/cp_codec_embedding_13.npy"] = "bf0454c373ff115501d1642675125146e92d91703442ffdf87a35a7b4fe94919",
                ["embeddings/cp_codec_embedding_14.npy"] = "57cb0ce39de77611a7be8f3932debe3a2fa9eba9189e7b3fac890c482ce9a8ba",
                ["embeddings/cp_codec_embedding_2.npy"] = "5ae15feb83dc7f97e6f939fe98c9dda91c08749c2a69384d71a8414166792825",
                ["embeddings/cp_codec_embedding_3.npy"] = "2d740ecfaa6591dcf014570a322192dd022f169bcca80849c41801ff496a866a",
                ["embeddings/cp_codec_embedding_4.npy"] = "3b466f7d3213b7a8b29381ee47338a9ac4de30460ac3d2defdaceaa0bab657e2",
                ["embeddings/cp_codec_embedding_5.npy"] = "494fde5327423a7462d5e3063dcd8f2ad87de411fe5093c587cf8e56b3ae53df",
                ["embeddings/cp_codec_embedding_6.npy"] = "e6d6780db3685caee084edbce3b2517e5fa52b6a077890dff9c14b6638bfb564",
                ["embeddings/cp_codec_embedding_7.npy"] = "4d10c51b112e7188062a712e0d664a22dd448cea52ed102e094943c871ec3577",
                ["embeddings/cp_codec_embedding_8.npy"] = "3bc9ba51d40d91be04f478d9330363af3d1ac1218144e7bf0c8b920bd4d1832a",
                ["embeddings/cp_codec_embedding_9.npy"] = "41add7203fe62f322fa66ad353d5a91f265413a5c711681769d5979e526ced51",
                ["embeddings/speaker_ids.json"] = "e6ca4dc700095ca487f6da18b146852a1e4ea3d11f827d654fbb071d91a4c199",
                ["embeddings/talker_codec_embedding.npy"] = "65a94ed3c86cb17e40504a581c746b274a4e56ba200c6f99f383a1bb6274cfbd",
                ["embeddings/text_embedding.npy"] = "210383e70be9dd7f5debcfcb24acb0de9c111836dd4920bc545b4cd8dd35956b",
                ["embeddings/text_projection_fc1_bias.npy"] = "e9a50b7668d089b5fc250f15a61941b97e573e43430587be06c404cbe447c5dc",
                ["embeddings/text_projection_fc1_weight.npy"] = "4b39093c0011eca2a955fcd272a7165271e3a37ff0d24ab51d5cfa60925b77af",
                ["embeddings/text_projection_fc2_bias.npy"] = "e66b17cef017c40c37970d27a626171d9a75ab4729fdab7843f8d1f9e9ce7963",
                ["embeddings/text_projection_fc2_weight.npy"] = "104adb52eabb786a4e26696b323e11a92e4b3568849c04960e907ac44d687b4d",
                ["talker_decode.onnx"] = "1a0ba437c0c39a011eddfbb43e2838e8eadfcc46021b2cdd5c7d562597935296",
                ["talker_decode.onnx.data"] = "389276b5ef745a38a5d9a5a4e5a39746ed590643f9cd3571eb8d6125c93bfd6e",
                ["talker_prefill.onnx"] = "cbd0bf1cd0ccc66a68d2238ae64622c6e033b7b50e1a0b7bc64d1758ec94c113",
                ["talker_prefill.onnx.data"] = "389276b5ef745a38a5d9a5a4e5a39746ed590643f9cd3571eb8d6125c93bfd6e",
                ["tokenizer/merges.txt"] = "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5",
                ["tokenizer/vocab.json"] = "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
                ["vocoder.onnx"] = "4ee20178c7ab322891ce412d92edcfbade2e5e94a8cffe054e17b760fc764e45",
                ["vocoder.onnx.data"] = "f4cd93d2b48b833a6aaca7d5a3c95dd99853baba565514cb91777e3ce3c4cc8d",
            },
            ["wavekat/Qwen3-TTS-1.7B-VoiceDesign-ONNX"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["config.json"] = "a31bc3d9967a3f00057017f962ed77d7f20ed1a10635ec61a088e689448c14d7",
                ["embeddings/cp_codec_embedding_0.npy"] = "d817a2fddbd2b6b4a48fc82eb6dae03c7e85976c3c978632ef8d3d463f405e8f",
                ["embeddings/cp_codec_embedding_1.npy"] = "dacdb94f57da9b2c52ac7122c44bbe5d0b0161c01bb959fcec20aadfefadd557",
                ["embeddings/cp_codec_embedding_10.npy"] = "223221fe7d3812e87ab768772ad43ece3bcdc4fd3a9a1553fdac728d5804dc06",
                ["embeddings/cp_codec_embedding_11.npy"] = "70032ec9f813bab56ae782c225afd6923d4fab61693ea7d9031b25e5801633d7",
                ["embeddings/cp_codec_embedding_12.npy"] = "8273bcfe29249ce1f40b8a2f50a221757cfd20a1470fff0dad0af1ee3fbd981b",
                ["embeddings/cp_codec_embedding_13.npy"] = "e98208b4cfe5b7ad0d587ff6102bffdef64cddbd501ec0a74a2b216717170259",
                ["embeddings/cp_codec_embedding_14.npy"] = "c08e5712f1aa9da944c8725e53e5c7294eee4260e4d686e8b2add990bc85550d",
                ["embeddings/cp_codec_embedding_2.npy"] = "cd159613ad05d6c13b110cfd897e58c2dcc4992b16b65a8885195e2a85bc4f22",
                ["embeddings/cp_codec_embedding_3.npy"] = "591f29341005ef44a08374f7db6fa09331bfd40099222a30841a6a30d468307f",
                ["embeddings/cp_codec_embedding_4.npy"] = "b3475def6be6832ec81be51ca1a578cc602dfd5e8da943514d54590263c494ea",
                ["embeddings/cp_codec_embedding_5.npy"] = "1c6b04c82af4377f570205b7fe012b19c1fbe145e55bd23ded18b17817afd02e",
                ["embeddings/cp_codec_embedding_6.npy"] = "818807b351bf14a35673f0947fdb56b5cd5603b89ee794479d75c74d19c1058b",
                ["embeddings/cp_codec_embedding_7.npy"] = "ab66016da143999347eb38855dbf4960043895b642ed217260b020020c43bc75",
                ["embeddings/cp_codec_embedding_8.npy"] = "78c897e85d9f7ed30d15f9d7d3b2c74b8ce36ff61314a7b81712b39cb4a7e22b",
                ["embeddings/cp_codec_embedding_9.npy"] = "034ca0184c8e546d6de7e61dcdcab8632260e034e91727ba0c7b6e9d7fef997c",
                ["embeddings/small_to_mtp_projection_bias.npy"] = "496229ae9d0269ac93f84f688a1481e0e8735cef50417ac2c7d12955e0352e4a",
                ["embeddings/small_to_mtp_projection_weight.npy"] = "e57c0515ff31947cd90ff730c9253ec533825788b4572fc7e49980107b42f188",
                ["embeddings/talker_codec_embedding.npy"] = "1140e81db3203d0e9118569f264271174588115d29b257a6a857be5fd4692a2f",
                ["embeddings/text_embedding.npy"] = "843354d7f837c2f8ac1e0708320226a98bf4b03057e34da59f8ff6dd197b2054",
                ["embeddings/text_projection_fc1_bias.npy"] = "3eb97f665af043f1f1242fbcc9fab2428558f70df7814ab3a21cfe559eabb52c",
                ["embeddings/text_projection_fc1_weight.npy"] = "3314a298c2fb0042924b2cd75aa8cdc065cec6bd545ae557ad5c5ddf4a1a988c",
                ["embeddings/text_projection_fc2_bias.npy"] = "fc5a6eae755bd96a9d8d0d36bb8dc63473e2146fb1d703f5e461a5fbcb70e522",
                ["embeddings/text_projection_fc2_weight.npy"] = "9f3205a265ffbf2c7e2a2f68f6c9a85913e14499a4225cf005ffd9f2613b597b",
                ["int4/code_predictor.onnx"] = "7fbf1d23c9d49a1f375cc2c936e2d9602c9ec2bf283ce940008349d5c68a661a",
                ["int4/code_predictor.onnx.data"] = "a08526a6b2e5f013117658930dfbe4ae3c24156f3f0ae6db92d0a2034baf0a32",
                ["int4/talker_decode.onnx"] = "b19a61dcff0425e9f75fadf14f7cfb40ef3602ca652652d09a695a559ebbaaef",
                ["int4/talker_decode.onnx.data"] = "df06bacf92ef2f5cd8b63840d39067593fecf6a22613ebff5a1ee0df2725080e",
                ["int4/talker_prefill.onnx"] = "8deacf194b588e0ce36fa3ba46f7754807190d33872d451c2b8f5301356a5ab0",
                ["int4/talker_prefill.onnx.data"] = "df06bacf92ef2f5cd8b63840d39067593fecf6a22613ebff5a1ee0df2725080e",
                ["int4/vocoder.onnx"] = "d1cf6f6b77d728c4484f9277ec886dcbf06b4f4b0310c99be34d7bc6a516959e",
                ["int4/vocoder.onnx.data"] = "d29e56c20bc6ff8d996daaae914d5d1ec1cd8170be578dc792b44978208a9575",
                ["tokenizer/added_tokens.json"] = "d7f75a97314f5a2c70018efccde8a1712364d1ee84d22623ca6390b8564b37c6",
                ["tokenizer/merges.txt"] = "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5",
                ["tokenizer/tokenizer.json"] = "09267689b8362020b9763b65dd5be7e086b31e28d72e02837a9e781de9a91bc7",
                ["tokenizer/vocab.json"] = "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
            },
            ["wavekat/Qwen3-TTS-0.6B-Base-ONNX"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["config.json"] = "9f6444a4e6c6a2bb238116957f2852713c447a581f791dcea088e6e6bf9bb55c",
                ["embeddings/cp_codec_embedding_0.npy"] = "2cf4b5d6f6fca977657bc82634eea275cd072036a6a16484febd41b5445abff1",
                ["embeddings/cp_codec_embedding_1.npy"] = "c8655ecd642f17d04a85ea37c2665205a8666843925227b25dc8c8bceb8015f5",
                ["embeddings/cp_codec_embedding_10.npy"] = "bf17275629b464a4a38826beeb79b99e0e58cc02c530da2e572b9b7d9972fb2d",
                ["embeddings/cp_codec_embedding_11.npy"] = "dee4af40f34626a4951fba2b894120c8cf8293024ae068d1f2579f356c6e2284",
                ["embeddings/cp_codec_embedding_12.npy"] = "e307b70fbaddc6e7e48259b4e8c9417f24b155be9281205f38bbf40c64359951",
                ["embeddings/cp_codec_embedding_13.npy"] = "cc15bfc4290be77523f8f14c78b9b1710042b5d078291ee87038dfb7ecb3f185",
                ["embeddings/cp_codec_embedding_14.npy"] = "aa0de98365775e1cb968514595ff7e23936bea21c2bda6e5bab7e7052cdf97ba",
                ["embeddings/cp_codec_embedding_2.npy"] = "5436624853b3091bc1b88cf37a4d60af46346c508237d72e597bed561a5b2138",
                ["embeddings/cp_codec_embedding_3.npy"] = "1041b78ea5158c96cda4aae45bd21707a6fe8c92e6831715075d70d7b5b8e77a",
                ["embeddings/cp_codec_embedding_4.npy"] = "17af8770454ab26ca82f092d0d5a634680e4d14412d327fb8b6f94a57b256800",
                ["embeddings/cp_codec_embedding_5.npy"] = "b9abbf163b50ba0207e2196772c67b2e1025cf7f4fbe378c6a44123869253ad9",
                ["embeddings/cp_codec_embedding_6.npy"] = "1fb06414cb7d4edcd73142e79cbb4bd46f918517e5812b73345ade9535c6eafb",
                ["embeddings/cp_codec_embedding_7.npy"] = "08b8d7bd125d75ffa42f515153e66ade42cb16c2eea6ebe1935a2ac2509cbad0",
                ["embeddings/cp_codec_embedding_8.npy"] = "a2cf331bb3e2f2c0d44c91c55af045b9c975199331e4566a373b883ce04e9964",
                ["embeddings/cp_codec_embedding_9.npy"] = "389422edef8c0b6dbf6cdb48359a0274b11984c04668c004810866784dab2942",
                ["embeddings/talker_codec_embedding.npy"] = "47fa9e30f98b1528fc9b332d314f22a32fa33e187509a4d3537f8b2c31199e39",
                ["embeddings/text_embedding.npy"] = "e49a5ebb22075609520b91db99c33cf7590cefddc96d574a725a353ff0b1f480",
                ["embeddings/text_projection_fc1_bias.npy"] = "2ca292516e9574fa67e0174a49770051156246bb0679096f0514286091eea7c3",
                ["embeddings/text_projection_fc1_weight.npy"] = "94b62d00b306c5db843ac86dc135354889a2ce40d7adce79ae23f63c3d6acf19",
                ["embeddings/text_projection_fc2_bias.npy"] = "0725b7f7d0318f430723177d046eaf3b08c7a65d4a4a6dd264d61e574e66ff19",
                ["embeddings/text_projection_fc2_weight.npy"] = "d6d31d3bca45fee1d05612f3c9eb26e0c34587be52d2c34c208c79e6656f15a6",
                ["int4/code_predictor.onnx"] = "6077ceb8fd1dd2c9e4533d564d917b913f4a4bf1056ee946f99dcb56dff3234f",
                ["int4/code_predictor.onnx.data"] = "78b530db39646d04ce8dbe60ae35e814ba05d832f345d828ed6ad69483af8b43",
                ["int4/talker_decode.onnx"] = "d37e92e3d9ac0ec80044f0f65f1d4cb86f5f9e804e82883b1f38bf59e4786271",
                ["int4/talker_decode.onnx.data"] = "7365dec88da097e8ff0f19e33cc6effe075ff387a8c6c0bb8b0725542be587b0",
                ["int4/talker_prefill.onnx"] = "4942858b31e4c000ada5f236e83208a3eb7da2016497304f8224c0a60374a5e8",
                ["int4/talker_prefill.onnx.data"] = "7365dec88da097e8ff0f19e33cc6effe075ff387a8c6c0bb8b0725542be587b0",
                ["int4/vocoder.onnx"] = "d1cf6f6b77d728c4484f9277ec886dcbf06b4f4b0310c99be34d7bc6a516959e",
                ["int4/vocoder.onnx.data"] = "d29e56c20bc6ff8d996daaae914d5d1ec1cd8170be578dc792b44978208a9575",
                ["speaker_encoder.onnx"] = "fa067d83976b96c98ba302ada0f03dde7c85189953fd1c7a178fc32e99cb8da8",
                ["speaker_encoder.onnx.data"] = "9f6f588deda5ebbcda0a6b010bded2669faf861f402cc5ba5556c58d5c0aeb54",
                ["tokenizer/added_tokens.json"] = "d7f75a97314f5a2c70018efccde8a1712364d1ee84d22623ca6390b8564b37c6",
                ["tokenizer/merges.txt"] = "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5",
                ["tokenizer/tokenizer.json"] = "09267689b8362020b9763b65dd5be7e086b31e28d72e02837a9e781de9a91bc7",
                ["tokenizer/vocab.json"] = "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910",
                ["tokenizer_encoder.onnx"] = "98284239976a637a229df22207b6ef34ff9a487890cfd00410e9c0facf7c182a",
                ["tokenizer_encoder.onnx.data"] = "4d8a454094b2f58265f4dab7545211f73331f78888a36a9039272818f51ecf2c",
            },
            ["ggerganov/whisper.cpp"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ggml-base.bin"] = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
            },
        };
}
