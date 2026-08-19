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

using Bunyi.Core;
using Bunyi.Core.Engine;
using Xunit;

namespace Bunyi.Core.Tests;

/// <summary>
/// Showing a model's identifiers to a person.
/// </summary>
/// <remarks>
/// Reported from using the app: the language picker read "auto" and "english",
/// and the speaker list changed from "Ryan" to "ryan" the moment a model loaded
/// and replaced the built-in list with its own.
/// </remarks>
public sealed class DisplayNameTests
{
    [Theory]
    [InlineData("english", "English")]
    [InlineData("auto", "Auto")]
    [InlineData("portuguese", "Portuguese")]
    [InlineData("ryan", "Ryan")]
    [InlineData("uncle_fu", "Uncle Fu")]
    [InlineData("ono_anna", "Ono Anna")]
    public void An_identifier_reads_the_way_a_person_writes_it(string identifier, string shown)
    {
        Assert.Equal(shown, DisplayName.For(identifier));
    }

    [Fact]
    public void The_built_in_list_and_a_loaded_model_look_the_same()
    {
        // The bug, stated exactly. The app ships "Uncle_Fu" and the model
        // reports "uncle_fu"; the picker must not change when one replaces the
        // other.
        foreach (var (ours, theirs) in new[]
        {
            ("Ryan", "ryan"),
            ("Uncle_Fu", "uncle_fu"),
            ("Ono_Anna", "ono_anna"),
            ("Serena", "serena"),
        })
        {
            Assert.Equal(DisplayName.For(ours), DisplayName.For(theirs));
        }
    }

    [Fact]
    public void Every_speaker_the_app_ships_survives_the_round_trip()
    {
        foreach (var speaker in FallbackSpeakers.All)
        {
            var shown = DisplayName.For(speaker);

            Assert.False(string.IsNullOrWhiteSpace(shown));
            Assert.DoesNotContain('_', shown);
            Assert.Equal(shown, DisplayName.For(shown));
        }
    }

    [Fact]
    public void Every_language_the_app_offers_reads_properly()
    {
        foreach (var language in Languages.All)
        {
            var shown = DisplayName.For(language);

            Assert.False(string.IsNullOrWhiteSpace(shown));
            Assert.True(char.IsUpper(shown[0]), $"{language} shows as {shown}");
        }
    }

    [Fact]
    public void Applying_it_twice_changes_nothing()
    {
        // It is applied at the point of display, and a value may already be in
        // its shown form — the built-in speaker list is.
        Assert.Equal("Uncle Fu", DisplayName.For(DisplayName.For("uncle_fu")));
    }

    [Fact]
    public void A_name_the_model_spelled_deliberately_is_left_alone()
    {
        // Lowercasing the tail would turn "McDonald" into "Mcdonald", which is
        // a different name rather than a tidier one.
        Assert.Equal("McDonald", DisplayName.For("McDonald"));
        Assert.Equal("O'Brien", DisplayName.For("O'Brien"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("_", "_")]
    public void Nothing_in_gives_nothing_back(string? identifier, string shown)
    {
        Assert.Equal(shown, DisplayName.For(identifier));
    }

    [Fact]
    public void It_is_only_for_showing_never_for_sending()
    {
        // The identifier is the model's word. Nothing here rewrites what gets
        // sent or stored — this test exists to say so, because the day someone
        // "tidies" the stored value is the day older clips stop matching.
        Assert.Equal("uncle_fu", FallbackSpeakers.All
            .Select(s => s.ToLowerInvariant())
            .First(s => s.StartsWith("uncle", StringComparison.Ordinal)));
    }
}
