{-# LANGUAGE NoImplicitPrelude #-}
{-# LANGUAGE OverloadedStrings #-}
module Arkham.Native.DeckBuild.Owned where
import Prelude
import Control.Monad (unless)
import Data.Aeson
import Data.Aeson.Types (parseEither,Parser)
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict (Map)
import Data.Map.Strict qualified as Map
import Data.Maybe (isJust)
import Data.Set qualified as Set
import Data.Text (Text)
import Data.Text qualified as T
import Arkham.Native.DeckBuild.Catalog
import Arkham.Native.DeckBuild.Types

data OwnedConstruction = OwnedConstruction
  { additionalOptions :: [Value], sizeDelta :: Int, singletonCap :: Maybe Int
  , brandedTarget :: Maybe Text, validatedMetaKeys :: [Text]
  , ownedIssues :: [Issue], ownedProvenance :: Value
  } deriving Show

deriveOwnedConstruction :: Catalog -> Map Text Int -> Object -> Either Text OwnedConstruction
deriveOwnedConstruction cat slots meta = do
  unless (all (\n -> n>=0 && n<=500) $ Map.elems slots) $ Left "Owned construction quantity out of input bounds"
  rows <- traverse contribution [(c,n) | (code,n)<-Map.toList slots,n>0
                                ,Just c<-[Map.lookup code $ catalogCards cat],cardType c/="investigator"]
  let missing=[Issue "ownedCardUnknown" code | (code,n)<-Map.toList slots,n>0,Map.notMember code $ catalogCards cat]
      (target,keys,attachmentIssues)=brand
  pure $ OwnedConstruction (concat [os | (os,_,_,_)<-rows]) (sum [n | (_,n,_,_)<-rows])
    (if present "08046" then Just 1 else Nothing) target keys
    (missing<>concat [es | (_,_,es,_)<-rows]<>attachmentIssues)
    (object ["contributions" .= [v | (_,_,_,v)<-rows],"sizeDelta" .= sum [n | (_,n,_,_)<-rows]
      ,"singletonCap" .= (if present "08046" then Just (1::Int) else Nothing)
      ,"attachmentFields" .= [object ["key" .= String "attachments_11080","ownerCode" .= String "11080"
         ,"selectedValue" .= KM.lookup "attachments_11080" meta
         ,"choices" .= (object ["value" .= String "","label" .= String "No branded spell"]
           : [object ["value" .= code,"label" .= cardName card]
              | (code,n)<-Map.toList slots,n>0,Just card<-[Map.lookup code $ catalogCards cat],brandEligible card])]
         | present "11080"]])
 where
  present code = Map.findWithDefault 0 code slots>0
  contribution (card,quantity) = do
    let code=cardCode card
        options=case cardOptions card of
          Nothing -> Right []
          Just (Array a) -> Right $ foldr (:) [] a
          _ -> Left "Owned deck_options must be an array"
        requirement=case cardRequirements card of
          Nothing -> Right (0,[])
          Just (Object o) -> do
            delta<-either (Left . T.pack) Right $ parseEither (\x -> x .:? "size" .!= 0 :: Parser Int) o
            unless (abs (toInteger delta)<=200) $ Left "Owned size adjustment exceeds product input bound"
            pure (delta,[Issue "ownedRequirementUnsupported" (code<>"."<>T.pack(show k)) | k<-KM.keys o,k/="size"])
          _ -> Left "Owned deck_requirements must be an object"
    case (options,requirement) of
      (Right os,Right (delta,issues)) -> pure (concat $ replicate quantity os,delta*quantity,issues,
        object ["code" .= code,"quantity" .= quantity,"sizeDelta" .= (delta*quantity),"optionCopies" .= (length os*quantity)])
      _ -> pure ([],0,[Issue "ownedDefinitionUnsupported" code],object ["code" .= code,"unsupported" .= True])
  brand = case KM.lookup "attachments_11080" meta of
    Nothing -> (Nothing,[],[])
    Just (String text) | T.length text<=16384 && present "11080" ->
      if T.null text then (Nothing,["attachments_11080"],[]) else
      case T.splitOn "," text of
        [code] | present code,Just card<-Map.lookup code $ catalogCards cat,brandEligible card ->
          (Just code,["attachments_11080"],[])
        _ -> unresolved "Expected one owned XP-bearing Spell asset (original attachment UI contract)"
    _ -> unresolved "Missing owner or malformed Eldritch Brand metadata"
  unresolved reason = (Nothing,[],[Issue "ownedAttachmentUnsupported" reason])

brandEligible :: CatalogCard -> Bool
brandEligible card = cardType card=="asset" && isJust(cardLevel card) && Set.member "spell" (cardTraits card)

rawField key card = case cardRaw card of Object o -> KM.lookup key o; _ -> Nothing

-- Keep original filterSlots' case-sensitive includes semantics, including its
-- explicit no-slot asset special case. Never infer slots from translated text.
hasSlot :: Text -> CatalogCard -> Bool
hasSlot slot card
  | slot == "none" = cardType card=="asset" && not (isJust $ nonemptySlot card)
  | otherwise = maybe False (T.isInfixOf slot) $ nonemptySlot card
 where
  nonemptySlot c = case rawField "real_slot" c of Just(String s) | not(T.null s) -> Just s; _ -> Nothing

copyLimitFor :: OwnedConstruction -> CatalogCard -> Maybe Int
copyLimitFor owned card
  | rawField "myriad" card == Just(Bool True) = Just $ maybe 3 id $ singletonCap owned
  | brandedTarget owned == Just(cardCode card) = Just 1
  | otherwise = case singletonCap owned of Just n -> Just n; Nothing -> cardLimit card

copyGroupName :: CatalogCard -> Either Text Text
copyGroupName card
  | cardCode card `elem` ["08114","08115"] = case rawField "real_subname" card of
      Just(String subtitle) -> Right $ cardName card<>" ("<>subtitle<>")"
      _ -> Left "Missing original English subtitle"
  | otherwise = Right $ cardName card

countsForSize :: CatalogCard -> Bool
countsForSize card = not(cardPermanent card) && not(isJust $ cardSubtype card)
  && isJust(cardLevel card) && case rawField "encounter_code" card of
    Nothing -> False
    Just Null -> True
    Just(String t) -> T.null t
    _ -> False
