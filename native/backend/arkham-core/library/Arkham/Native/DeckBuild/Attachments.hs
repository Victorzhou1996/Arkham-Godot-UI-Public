{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Attachments where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Key qualified as Key
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Maybe (fromMaybe)
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types (Issue(..))

data AttachmentPlan = AttachmentPlan
  { fields :: [Value], validatedMetaKeys :: [Text], issues :: [Issue] } deriving Show
data Definition = Definition Text Text Text Int (Maybe Int) [Text] (Maybe Text) Bool
definitions :: [Definition]
definitions =
  [ Definition "03264" "Stick to the Plan" "lucide://package" 3 (Just 1) ["Tactic.","Supply."] (Just "event") False
  , Definition "05002" "Hunch deck" "lucide://lightbulb" 11 Nothing ["Insight."] (Just "event") False
  , Definition "09077" "Market deck" "lucide://store" 10 Nothing ["Illicit."] Nothing False
  , Definition "10079" "Bewitching" "lucide://wand" 3 (Just 1) ["Trick."] Nothing False
  , Definition "11080" "Eldritch Brand" "lucide://stamp" 1 (Just 1) ["Spell."] (Just "asset") False
  , Definition "07303" "Ancestral Knowledge skill preselection" "" 5 Nothing [] (Just "skill") True
  ]
ownerOf :: Definition -> Text
ownerOf (Definition c _ _ _ _ _ _ _) = c
attachmentKey :: Text -> Text
attachmentKey = ("attachments_" <>)
rawField :: Text -> CatalogCard -> Maybe Value
rawField key c = case cardRaw c of Object o -> KM.lookup (Key.fromText key) o; _ -> Nothing
literal :: Definition -> Value
literal (Definition code name icon target lim traits ty _) = object $
  ["code" .= code,"name" .= name,"icon" .= icon,"targetSize" .= target,"traits" .= traits]
  <> maybe [] (\n -> ["limit" .= n]) lim
  <> ["requiredCards" .= object ["05010" .= (1::Int)] | code=="05002"]
  <> ["filters" .= (maybe [] (\t -> [object ["attribute" .= ("type_code"::Text),"value" .= t]]) ty
       <> [object ["attribute" .= ("xp"::Text),"value" .= Null,"operator" .= ("!="::Text)] | code=="11080"]) | ty/=Nothing]

decodeAttachmentCodes :: Value -> Either Text [Text]
decodeAttachmentCodes (String "") = Right []
decodeAttachmentCodes (String t) = do
  let xs=T.splitOn "," t
  unless (T.length t<=16384 && length xs<=500 && all valid xs) $ Left "Invalid attachment comma list"
  pure xs
 where valid x=not(T.null x) && T.length x<=160 && T.strip x==x && not(T.any (\c -> c<' ' || c=='\DEL') x)
decodeAttachmentCodes _ = Left "Attachment metadata must be a string"

-- Catalog is already taboo/customization projected. No mutation or rule execution.
deriveAttachmentPlan :: Catalog -> Text -> Map Text Int -> Object -> Either Text AttachmentPlan
deriveAttachmentPlan cat investigator mainSlots meta = do
  unless (all (\n -> n>=0 && n<=500) (Map.elems mainSlots)) $ Left "Invalid main-deck quantities"
  rows <- traverse prepare [d | d<-definitions, owned d || KM.member (Key.fromText $ attachmentKey $ ownerOf d) meta]
  let totals=Map.fromListWith (+) [(c,1::Int) | (_,_,xs,_,_,_)<-rows,c<-xs]
      containers c=length [() | (_,_,xs,_,_,_)<-rows,c `elem` xs]
      cross c=containers c>1 && Map.findWithDefault 0 c totals >
        min (Map.findWithDefault 0 c mainSlots) (maybe 0 (fromMaybe 0 . cardLimit) $ Map.lookup c $ catalogCards cat)
      finish (d@(Definition owner name _ target _ _ _ extension),present,xs,candidates,errs,definitionOK)=
        let conflicts=[Issue "attachmentConflict" (owner<>" shares too many copies of "<>c) | c<-Map.keys $ Map.fromList [(x,()) | x<-xs],cross c]
            allErrors=errs<>conflicts
            requiredOK=owner/="05002" || length(filter (=="05010") xs)==1
            complete=length xs==target && requiredOK && null allErrors
            status | not(null allErrors)="unsupported" | null xs="unselected" | complete="complete" | otherwise="fallback"
            value=object ["key" .= attachmentKey owner,"ownerCode" .= owner,"name" .= name
              ,"source" .= (if extension then "original-game-optional-preselection" else "fixed-build-attachment-definition"::Text)
              ,"targetCount" .= target,"selectedCodes" .= xs
              ,"requiredCounts" .= (if owner=="05002" then Map.singleton ("05010"::Text) (1::Int) else Map.empty)
              ,"candidates" .= candidates,"status" .= (status::Text),"issues" .= allErrors]
        in (value,[attachmentKey owner | present && definitionOK && null allErrors],allErrors)
      done=map finish rows
  pure $ AttachmentPlan [v | (v,_,_)<-done] (concat [ks | (_,ks,_)<-done]) (concat [es | (_,_,es)<-done])
 where
  owned d | ownerOf d=="05002" = investigator=="05002"
          | otherwise = Map.findWithDefault 0 (ownerOf d) mainSlots>0
  prepare d@(Definition owner _ _ target lim traits ty extension) = do
    let supplied=KM.lookup (Key.fromText $ attachmentKey owner) meta
    xs<-maybe (Right []) decodeAttachmentCodes supplied
    let ownerCard=Map.lookup owner $ catalogCards cat
        definitionOK=case ownerCard of
          Nothing -> False
          Just c | extension -> rawField "attachments" c `elem` [Nothing,Just Null]
                 | otherwise -> rawField "attachments" c==Just(literal d)
        candidate c=cardCode c/=owner && maybe True (==cardType c) ty
          && (null traits || case rawField "traits" c of Just(String s)->any (`T.isInfixOf` s) traits;_->False)
          && (owner/="11080" || cardLevel c/=Nothing)
          && (not extension || cardSubtype c `notElem` [Just "weakness",Just "basicweakness"])
        cap c n | extension = min n 5
                | otherwise = minimum [n,fromMaybe 0(cardLimit c),fromMaybe maxBound lim]
        allowed=Map.fromList [(code,cap c n) | (code,n)<-Map.toList mainSlots,n>0
          ,Just c<-[Map.lookup code $ catalogCards cat],candidate c]
        counts=Map.fromListWith (+) [(c,1::Int) | c<-xs]
        errs=[Issue "attachmentOwner" owner | not(owned d)]
          <> [Issue "attachmentDefinitionUnsupported" owner | not definitionOK]
          <> [Issue "attachmentCandidate" (owner<>":"<>c) | (c,n)<-Map.toList counts,n>Map.findWithDefault 0 c allowed]
          <> [Issue "attachmentQuantity" owner | length xs>target]
          <> [Issue "attachmentRequiredQuantity" owner | owner=="05002"
             && (Map.findWithDefault 0 "05010" counts>1
                 || length xs==target && Map.findWithDefault 0 "05010" counts/=1)]
        candidates=[object ["code" .= c,"maxQuantity" .= n,"fixedQuantity" .= (if owner=="05002" && c=="05010" then 1 else 0::Int)] | (c,n)<-Map.toList allowed]
    pure (d,supplied/=Nothing,xs,candidates,errs,definitionOK)
