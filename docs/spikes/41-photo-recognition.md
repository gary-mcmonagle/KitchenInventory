# Spike #41: Photo recognition — identify products (several per photo) from a picture

Card: https://trello.com/c/HUiOS8Pn

## Question

What's the best way to identify **what's in a phone photo**: a packed product, loose fruit and veg, or several items at once? The answer is confirmed with checkboxes before anything is added. Gary's steer (2026-10-18):

- **What the food is matters more than the brand.**
- Lean towards a **Foundry vision model**, with Jev if needed.
- The camera is the **first choice for loose items**.

## Summary

- **Recommendation: gpt-4o (2024-11-20) on Azure AI Foundry, as a Standard deployment in UK South**, with the "v2" prompt and structured JSON output below.
  - **One call per photo** returns every item, with its kind, quantity and (when legible) brand and size.
  - Gary confirms the items with **checkboxes** before anything is added.
  - **Shelf life comes from Jev** (#52's estimator), not the vision model.
- **Accuracy** (v2 prompt, 13 of Gary's photos, 17 items):

  | | gpt-4o | gpt-4.1-mini |
  |---|---|---|
  | What each item is | 17/17 correct | 16/17 (missed a potato) |
  | Loose items | All named | All named |
  | Made-up details | 1 (a borrowed brand) | Typos and wilder shelf lives |

  Two runs of gpt-4o gave the same items and names.
- **Ruled out:**
  - **OCR → Jev** (Document Intelligence Read, then Jev picks lines): it picks fragments ("BRAN", "PRIVA", "Pitte") and can't do loose items at all.
  - **Jev with images:** Jev is text only.
  - **Hybrid** (OCR text plus the image): no better than the image alone, sometimes worse.
- **Self-reported confidences aren't reliable.** gpt-4o gave 1.0 to a wrong brand. The UI shouldn't use them for "Looks right". The checkboxes plus editable fields are the safeguard.
- **Cost:** about $0.006 per photo with gpt-4o (measured tokens), so a few dollars a year at household volume. Nothing while idle.
- **Latency:** 1.5–3.0 s per photo.
- **Privacy:** the photo goes to Microsoft's Azure in the UK. It's processed in the resource's geography with a Standard deployment, isn't used for training, isn't sent to OpenAI, and isn't stored by the model. We never store photos.

## How it was tested

- **Script:** `docs/spikes/41-photo-recognition.py` (standard library only).
  - Photos are read from a folder outside the repo, already resized to a 1024 px long edge (JPEG, 115–260 KB).
  - Auth is an Entra token from `az account get-access-token`. The resource has local auth disabled, so there are no keys anywhere.
  - Raw responses are cached outside the repo. **Photos and responses are never committed.**
- **Infra:** `ais-cupboard-ugxum7` (AI Services kind, `infra/modules/aiServices.bicep`), with `gpt-4o` 2024-11-20 and `gpt-4.1-mini` 2025-04-14 as Standard deployments at 10K tokens per minute each. It also includes Document Intelligence for the OCR comparison.
- **Approaches** (2026-10-18):

  | Run | What |
  |---|---|
  | `gpt-4o`, `gpt-4.1-mini` (v1 prompt) | The image, structured output |
  | `hybrid-gpt-4o` | OCR lines plus the image |
  | `ocr+jev` | Document Intelligence Read (`prebuilt-read`), then Jev chooses name, brand and size lines (#40's v2 prompt) |
  | `v2-gpt-4o`, `v2-gpt-4.1-mini`, `v2b-gpt-4o` | The v2 prompt (below); `v2b` is a repeat run, to check it's repeatable |

- **Photos:** 13, from Gary. Ground truth is from looking at the photos, with Gary's corrections (Fruit & Nut 180 g, olives 340 g, the wine is Riserva Privada, no soy sauce in the rice photo).
  - **Gap:** no photo had an **in-store printed label** (deli or weighed). The baguette was in a plain paper bag. See follow-ups.

## Results (v2 prompt; what the item is first, as Gary asked)

✔ correct · ~ partial · ✘ wrong · – not visible / correctly left blank

| Photo | Truth | gpt-4o: item | gpt-4o: brand · size | gpt-4.1-mini: item | gpt-4.1-mini: brand · size | OCR → Jev (name) |
|---|---|---|---|---|---|---|
| Bran flakes | Crownfield bran flakes, 500 g | ✔ Bran Flakes | ✔ Crownfield · ✔ 500 g | ✔ bran flakes cereal | ✔ · ✔ | ~ "BRAN" |
| Pralines | J.D. Gross milk chocolate pralines | ✔ milk chocolate pralines | ✔ · – | ✔ | ✔ · – | ~ "Pralines" |
| Fruit & Nut + potato | Cadbury Dairy Milk Fruit & Nut 180 g; a potato at the edge | ✔ milk chocolate fruit and nut bar; ✔ potato | ✔ Cadbury · – | ✔ bar; ✘ **missed the potato** | ✔ · – | ✔ "Cadbury DAIRY MILK FRUIT & NUT" |
| Potatoes | 2 potatoes | ✔ 2× potatoes | – | ✔ 2× potato | – | ✘ nothing |
| Baguette | baguette in a paper bag | ✔ baguette (loose) | – | ✔ baguette (as "packaged") | – | ✘ "Potatoes" (the tin behind) |
| Leeks + orange | 2 leeks, 1 orange | ✔ 2× leeks; ✔ orange | – | ✔ ✔ | – | ✘ nothing |
| Wine | Riserva Privada Cabernet Sauvignon | ✔ cabernet sauvignon red wine | – · – | ✔ | – · – | ~ "CABERNET" |
| Peanut butter + honey | Maribel crunchy peanut butter 1 kg; Mazurskie Miody Polish honey | ✔ crunchy peanut butter; ✔ Polish honey | ✔ Maribel · ✔ 1kg; ✔ Mazurskie Miody · – | ~ peanut butter; ~ honey | ✔ · ✔; ✔ · – | ~ "HONEY" only |
| Fridge | Dairy Manor soured cream; 2× ASDA tempura chicken tenders; apples | ✔ soured cream; ✔ 2× tempura chicken tenders; ✔ apples (5) | **✘ ASDA** (borrowed from the chicken) · 300ml (unverified); ✔ ASDA · – | ✔ ✔ ✔ (6 apples) | ~ "Dairy Manner" · –; ✔ · – | ~ "Soured cream" only |
| Olives | Baresa pitted green olives 340 g (label turned away) | ✔ pitted green olives | – · – | ✔ | – · – | ✘ "Pitte" |
| Sundried tomatoes | Baresa sundried tomatoes | ✔ | ✔ Baresa · – | ✔ | ✔ · – | ~ "TOMATOES" |
| Pasta pouch | Newgate tomato & mascarpone pasta 200 g | ✔ | ✔ · ✔ | ✔ | ✔ · ✔ | ~ "PASTA" |
| Rice | Taste of basmati rice 1 kg (other packs behind) | ✔ basmati rice | ✔ · ✔ | ✔ | ✔ · ✔ | ~ "Rice" |

**Totals, items identified:** gpt-4o v2 **17/17**; gpt-4.1-mini v2 16/17; OCR → Jev 4 right, 6 partial, 3 wrong or nothing (and it can only ever return one item).

**The v1 prompt was worse in ways v2 fixed:**

| Problem in v1 | v2 |
|---|---|
| Generic names ("chocolate bar", "peanut butter") | Specific names ("milk chocolate fruit and nut bar", "crunchy peanut butter") |
| Made-up sizes (Fruit & Nut "300g", conf 1.0) and brands (olives "Lidl", "Tesco 340g" from mini) | Leaves brand and size blank unless legible |
| A soy sauce that wasn't there | Gone ("leave out anything you can only guess at") |
| The baguette as "storeLabelled" | Loose |

**Still wrong in v2:**
- **The soured cream's brand.** gpt-4o took "ASDA" from the chicken next to it, in both runs.
- **The text "null"** instead of JSON null, now and then.

**Repeatability:** `v2-gpt-4o` and `v2b-gpt-4o` gave the same items, kinds and brands on all 13 photos. Only capitalisation and two shelf-life numbers differed.

**Hybrid (v1):** the OCR text didn't help. It made the wine generic ("red wine"), branded the olives "Lytos", and kept the soured cream as ASDA.

## Answers to the card's questions

1. **Accuracy:** the table above. gpt-4o is best at *what the item is*, which is what matters (Gary). Brand and size are right when legible, and blank otherwise, apart from one borrowed brand.
2. **Loose items:** yes, both vision models (potatoes, leeks, orange, apples, baguette). OCR can't.
3. **Structured output and confidence:**
   - **Structured output:** yes. `response_format: json_schema` with `strict: true` always returned valid JSON in the schema.
   - **Per-field confidence:** the model fills it in, but **it's not calibrated**: 1.0 on a wrong brand, and 0 for "not visible". Don't drive "Looks right" from it.
   - **Shelf life in the same call:** possible, but the values were inconsistent (gpt-4.1-mini: honey 3 years, apples 30 days; gpt-4o sometimes left it null). **Use Jev's shelf-life estimator (#52) on the confirmed name instead.** It's already built and tested, so drop `shelfLifeDays` from the vision schema.
4. **Latency and cost** (measured):

   | | gpt-4o | gpt-4.1-mini | Document Intelligence Read |
   |---|---|---|---|
   | Latency per photo | 1.5–3.0 s | 1.2–2.0 s | ~1.3 s |
   | Tokens per photo (in / out) | 1,347 / 55–150 | 1,692 / 55–160 | – |
   | Cost per photo (UK South, Standard) | ≈ $0.004 + $0.002 = **$0.006** | ≈ $0.001 | $0.0015 |
   | 1,000 photos a year | **≈ $6** | ≈ $1 | ≈ $1.50 |

   Prices: gpt-4o 2024-11-20 regional $0.003025 per 1K input tokens and $0.0121 per 1K output; gpt-4.1-mini regional $0.000484 / $0.001936; Read $1.50 per 1,000 pages (Azure retail prices API, 2026-10-18). There's no fixed cost.
5. **Privacy** (Microsoft, *Data, privacy, and security for Foundry Models sold by Azure*):
   - Prompts and completions (including images) are "NOT available to OpenAI", "NOT used … to improve their models", and the models are "stateless: no prompts or completions are stored in the model".
   - With a Standard deployment they're "processed within the customer-specified geography" (UK). Global and DataZone deployments may process them elsewhere, which is why we use Standard.
   - Abuse monitoring may keep **flagged** samples for review, stored in the resource's geography.
   - We send the resized photo, use the answer and **never store the photo** (not in Cosmos, Blob or logs).
6. **Practicalities:**
   - **Auth:** managed identity (the API app gets *Cognitive Services User* on the account), `disableLocalAuth: true`, no keys or Key Vault secret.
   - **SDK:** `Azure.AI.OpenAI` (or plain `HttpClient` like the Jev client) with `DefaultAzureCredential`.
   - **Images:** the browser resizes to a 1024 px long edge as JPEG (quality ~0.8, ~150–250 KB) before upload. That keeps the request small and the tokens at about 1,300, and reads labels well with `detail: high`. iPhone camera capture in the browser gives JPEG, so HEIC isn't an issue in the app.
   - **Region:** both models are available as Standard in UK South (checked with `az cognitiveservices model list`).
   - **Retirement:** model versions retire. The deployment is pinned (`versionUpgradeOption: NoAutoUpgrade`) and the version goes in config, so moving to a newer version is a Bicep change plus a re-check on these photos (keep the script).
   - Azure AI Vision Image Analysis is not needed: the vision model does more, in our region.

## Recommended design

**Several items in one photo, confirmed with checkboxes** (Gary's request):

1. The app takes or picks a photo, resizes it to 1024 px and sends it to `POST /api/photos/recognise`. The API answers in 2–3 s and stores nothing.
2. The response lists the items found:
   ```json
   { "readable": true,
     "items": [
       { "name": "milk chocolate fruit and nut bar", "brand": "Cadbury", "size": null, "kind": "packaged", "quantity": 1 },
       { "name": "potato", "brand": null, "size": null, "kind": "loose", "quantity": 1 } ] }
   ```
3. The app shows a **checklist**: each item ticked, with an editable name, quantity (stepper), and brand and size shown as optional. Gary unticks anything wrong and fixes names, then **Add**.
4. **On Add, each ticked item:**
   - **Matches an existing product** by name (case-insensitive), or for loose items creates a new **loose product** (no barcode).
   - **Gets units** at quantity.
   - **Gets its shelf life** from Jev's estimator (#52), either straight away or via the timer.
5. **For an unknown barcode (#56), the same recogniser runs on a photo of that item.** The first item it returns pre-fills Name it (name, brand, size), and Gary confirms or edits.

**Interface** (Application; the implementation in Infrastructure):

```csharp
public interface IProductPhotoRecogniser
{
    Task<PhotoRecognition> RecogniseAsync(ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken);
}

public sealed record PhotoRecognition(bool Readable, IReadOnlyList<RecognisedItem> Items);
public sealed record RecognisedItem(string Name, string? Brand, string? Size, ItemKind Kind, int Quantity);
public enum ItemKind { Packaged, Loose, StoreLabelled, NotFood }
```

**Infrastructure:**
- Clean the model's output: treat "null", "unknown" and "" as null, trim, cap lengths, and clamp the quantity to 1–20.
- Retry 429 and 5xx. On a timeout, return "couldn't read the photo" (the app falls back to typing).
- Never log the image. Log tokens, latency and the number of items.

**Prompt and schema:** `INSTRUCTIONS_V2` and `SCHEMA` in the script. For production, drop the confidence fields and `shelfLifeDays` (unused, see above).

**Bicep:**
- The account already exists (`infra/modules/aiServices.bicep`).
- Add a role assignment for the **API app's** identity (*Cognitive Services User*).
- Add app settings `AI_SERVICES_ENDPOINT` and `PHOTO_MODEL_DEPLOYMENT=gpt-4o`.
- Optionally drop the `gpt-4.1-mini` deployment, and Gary's own role, once the spike is closed.
- No secrets.

## Suggested follow-up cards (not created)

1. **[Task] Photo recogniser in the API.**
   - `IProductPhotoRecogniser` (gpt-4o, managed identity) and `POST /api/photos/recognise` (multipart JPEG, ≤ 1 MB, bearer token).
   - Bicep: the API app's role and settings.
   - Output clean-up, tests with recorded responses, `docs/api.md`.
2. **[Feature] Add from a photo (camera first), with checkboxes.** This replaces #63 "Add a loose item from a photo" and the quick-pick-first parts of #58.
   - The Add tab opens the camera.
   - The checklist as above.
   - Matching existing products by name, or creating loose products.
   - Units and shelf life via Jev.
   - A typed "Add by name" fallback.
3. **[Feature] Identify an unknown item from a photo (#56):** update it to use this recogniser (not OCR → Jev), pre-filling Name it.
4. **[Spike or small task] Store labels:** photograph 2–3 real deli or weighed labels and check whether the recogniser reads the product name from them. None were in this sample. #58's store-label handling (EAN-13 20–29) still applies to scans.
5. **Tidy-up:** once the follow-ups use it, remove `gpt-4.1-mini` and Gary's spike role from the Bicep.
