module Arkham.Compatibility.HomebrewIds (canonicalHomebrewId) where

import Arkham.Prelude
import Data.Text qualified as T

-- The upstream Oct8 rename changes campaign, scenario, card and encounter-set
-- identities. Normalize only that exact namespace at typed decoding boundaries;
-- never rewrite user names, narrative text, or the raw undo patch history.
canonicalHomebrewId :: Text -> Text
canonicalHomebrewId value
  | value == old = current
  | Just suffix <- T.stripPrefix (old <> ":") value = current <> ":" <> suffix
  | otherwise = value
 where
  old = ":return-to-innsmouth"
  current = ":return-to-the-innsmouth-conspiracy"
