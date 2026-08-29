using UnityEngine;
using UnityEngine.InputSystem;

// The 1 / 2 single-sided / double-sided hotkeys, in ONE place.
//
// These used to live in HairCard.Update(), which meant Unity dispatched a managed Update
// callback into every single hair card every single frame just to ask the same global
// question - "was the 1 key pressed?" - and get the same answer N times. That is a cost
// that scales linearly with the size of the groom, is paid whether or not the cards are
// even visible, and buys nothing.
//
// One listener, one poll per frame, same broadcast on the frame the key is pressed.
[DefaultExecutionOrder(-900)]
public class HairCardSidednessHotkeyAuthority : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Spawn()
    {
        if (FindFirstObjectByType<HairCardSidednessHotkeyAuthority>() != null) return;
        GameObject go = new GameObject("HairCardSidednessHotkeyAuthority");
        DontDestroyOnLoad(go);
        go.AddComponent<HairCardSidednessHotkeyAuthority>();
    }

    void Update()
    {
        if (Keyboard.current == null) return;

        // The 1/2 hotkeys must not fire while the user is typing into a UI text field -
        // for example the inline group-name editor, where "1" and "2" are just characters
        // in a name.
        if (GroupNameInlineEditAuthority.IsEnteringText) return;

        // Nor underneath the demo's buy card. This one flips EVERY card in the scene, so a
        // stray keypress while reading a modal is an edit to the whole groom with nothing on
        // screen to connect the two. Always false in a PRO build.
        if (DemoUpgradePrompt.IsOpen) return;

        bool wantSingleSided = Keyboard.current.digit1Key.wasPressedThisFrame || Keyboard.current.numpad1Key.wasPressedThisFrame;
        bool wantDoubleSided = Keyboard.current.digit2Key.wasPressedThisFrame || Keyboard.current.numpad2Key.wasPressedThisFrame;
        if (!wantSingleSided && !wantDoubleSided) return;

        // The same refusal the SS/DS button gives under DIAMOND, and it has to be here too:
        // this key writes every card in the scene directly, so without it 2 would set every
        // card double sided until GroupSidednessAuthority's next sweep put it back - a
        // scene-wide flash of the exact state the diamond exists to make unnecessary, plus a
        // material write per card, twice, for nothing.
        if (HairCardSection.IsDiamond)
        {
            StatusToast.Show("DIAMOND cards are single sided - every face already points outward. Switch the CARD profile to TENT to use 1 / 2.", true, 5f);
            return;
        }

        bool doubleSided = true;
        if (wantSingleSided) doubleSided = false;

        foreach (HairCard card in FindObjectsByType<HairCard>(FindObjectsSortMode.None))
        {
            if (card == null) continue;
            card.SetDoubleSided(doubleSided);
        }
    }
}
