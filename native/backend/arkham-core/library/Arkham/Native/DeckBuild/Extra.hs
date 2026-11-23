{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Extra where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither,Parser)
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types

data ExtraPlan = ExtraPlan
  { extraEnabled :: Bool, extraWireCodes :: [Text], extraQuantities :: Map Text Int
  , extraOptions :: [Value], extraRequirements :: Maybe Value
  , extraTargetCount :: Maybe Int, extraPerCodeLimit :: Maybe Int
  , extraValidatedMetaKeys :: [Text], extraIssues :: [Issue]
  } deriving Show

decodeExtraDeck :: Value -> Either Text [Text]
decodeExtraDeck (String "") = Right []
decodeExtraDeck (String text) = do
  unless (T.length text<=16384) $ Left "Extra deck text exceeds input bound"
  let codes=T.splitOn "," text
  unless (length codes<=500 && all valid codes) $ Left "Invalid extra deck card list"
  pure codes -- preserve order AND repetitions; do not deduplicate/rewrite original meta.
 where
  valid code=not(T.null code) && T.length code<=160 && T.strip code==code
    && not(T.any (\c->c<' ' || c=='\DEL') code)
decodeExtraDeck _ = Left "Extra deck metadata must be a comma-separated string"

-- Actual back must already be resolved (parallel/taboo). This module is NOT a matcher.
deriveExtraPlan :: Catalog -> CatalogCard -> Object -> Either Text ExtraPlan
deriveExtraPlan cat back meta = do
  let input=KM.lookup "extra_deck" meta
  codes<-maybe (Right []) decodeExtraDeck input
  unless (all (`Map.member` catalogCards cat) codes) $ Left "Unknown extra deck card code"
  let quantities=Map.fromListWith (+) [(c,1) | c<-codes]
      consumed=["extra_deck" | input/=Nothing]
      pending reason=ExtraPlan False codes quantities [] Nothing Nothing Nothing [] [Issue "extraDeckUnsupported" reason]
      empty=ExtraPlan False codes quantities [] Nothing Nothing Nothing consumed []
      field key=case cardRaw back of Object o->KM.lookup key o; _->Nothing
      absent value=value==Nothing || value==Just Null
      opts=field "side_deck_options"; req=field "side_deck_requirements"
  if absent opts && absent req
    then pure $ if null codes then empty else pending "Actual investigator back has no extra-deck definition"
    else if cardCode back/="90049"
      then pure $ pending "Extra-deck definition has not been adapted for this exact back"
      else case (opts,req) of
        (Just (Array options),Just requirements@(Object requirementsObject)) -> do
          let originalOption=object ["level" .= object ["min" .= (0::Int),"max" .= (2::Int)]
                ,"trait" .= (["ally"]::[Text])]
              originalRequirements=object ["size" .= (9::Int),"card" .= object ["90053" .= object ["90053" .= ("90053"::Text)]]]
          if toJSON options/=toJSON [originalOption] || requirements/=originalRequirements
            then pure $ pending "Changed or unknown Jim extra-deck definition requires an adapter review"
            else do
              size<-either (Left . T.pack) Right $ parseEither (\o->o .: "size" :: Parser Int) requirementsObject
              pure $ ExtraPlan True codes quantities (foldr (:) [] options) (Just requirements)
                (Just $ size+1) (Just 1) consumed []
        _ -> pure $ pending "Incomplete or malformed extra-deck definition"
