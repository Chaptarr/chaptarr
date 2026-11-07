# Localization language availability and RTL gate

The UI language selector now shows each language's native name and English name. New choices are limited to locales whose resolved catalog contains at least one real non-English UI value; locale variants that inherit a translated parent remain available. The four empty catalogs (Azerbaijani, Kazakh, Malay, and Uzbek) no longer appear as new choices. Existing saved values remain visible so users can move them to a supported language without invalidating settings.

Arabic keeps its `lang` metadata, but the application and login document roots stay left-to-right until shared layouts use direction-safe logical positioning and pass headed visual review. Per-field direction handling and mixed-script isolation remain active. This note does not claim full RTL acceptance.
