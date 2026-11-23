-- Local, explicitly trusted author packages. Preview is pure: no registration,
-- CardId generation, expression execution or mutation. The original custom DSL
-- remains the runtime; reflected schema metadata is NOT a safety validator.
module Arkham.Native.Author where

import Arkham.Prelude
import Arkham.Card
import Arkham.Custom.Schema qualified as Schema
import Arkham.Native.StrictJSON (validateJsonProperties)
import Arkham.Name (toTitle)
import Crypto.Hash.SHA256 qualified as SHA
import Data.ByteString.Base16 qualified as B16
import Data.ByteString qualified as BS
import Data.ByteString.Lazy qualified as BL
import Data.Text.Encoding qualified as TE
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Map.Strict qualified as Map
import Data.Aeson.Key qualified as Key
import Data.Text qualified as T
import Control.Monad.Fail (fail)

data AuthorPackage=AuthorPackage
  { authorRaw :: Text
  , authorDigest :: Text
  , authorCards :: Map CardCode CustomCard
  , authorIgnoredFields :: [Text]
  }

rawDigest :: Text -> Text
rawDigest=TE.decodeUtf8 . B16.encode . SHA.hash . TE.encodeUtf8

boundedDepth :: Int -> Value -> Either Text ()
boundedDepth depth value=do
  unless (depth<=64) $ Left "Author JSON nesting exceeds64"
  case value of
    Object o->traverse_ (boundedDepth (depth+1)) $ KM.elems o
    Array a->traverse_ (boundedDepth (depth+1)) a
    _->pure ()

supportedTypes :: [CardType]
supportedTypes=[AssetType,EventType,SkillType,InvestigatorType,PlayerEnemyType,PlayerTreacheryType,
  EnemyType,TreacheryType,LocationType,EnemyLocationCardType,EncounterAssetType,EncounterEventType,StoryType,KeyType]

-- Exact named fields read by the fixed CardDef FromJSON. Diagnostic metadata
-- only: never filter advanced meta or replace original typed parsing.
knownDefFields :: [Text]
knownDefFields=T.words "cardCode name revealedName cost additionalCost level cardType cardSubType classSymbols skills cardTraits revealedCardTraits keywords fastWindow actions revelation victoryPoints vengeancePoints criteria overrideActionPlayableIfCriteriaMet commitRestrictions attackOfOpportunityModifiers permanent encounterSet encounterSetQuantity unique doubleSided limits exceptional uses playableFromDiscard stage slots alternateCardCodes replacementCardCode art artVariants backArtVariants locationSymbol locationRevealedSymbol locationConnections locationRevealedConnections purchaseTrauma grantedXp canReplace deckRestrictions bondedWith skipPlayWindows beforeEffect customizations otherSide duplicateOf whenDiscarded canCommitWhenNoIcons commitTrigger meta options tags cardInHandEffects cardInDiscardEffects cardInSearchEffects outOfPlayEffects health fight evade healthDamage sanityDamage alternateSkills alternateErrata errata"

ignoredFields :: Value -> [Value] -> [Text]
ignoredFields root entries=unknown "$" ["format","version","cards"] root<>concat
  [cardUnknown index entry|(index,entry)<-zip [0::Int ..] entries]
 where
  cardUnknown index entry=let prefix="$.cards["<>tshow index<>"]" in
    unknown prefix ["def","art"] entry<>(case entry of
      Object o->maybe [] (unknown (prefix<>".def") knownDefFields) $ KM.lookup "def" o
      _->[])
  unknown path known (Object o)=[path<>"."<>Key.toText key|key<-KM.keys o,Key.toText key `notElem` known]
  unknown _ _ _=[]

canonicalCode :: CardCode -> Either Text ()
canonicalCode code=do
  let text=unCardCode code
  unless (isCustomCardCode code && T.length text>=2 && T.length text<=160 && T.all (\c->c>' ' && c<'\127') text) $ Left "Author card requires a bounded custom *code"
  unless (unCardCode (sanitizeCustomCardCode code)==text) $ Left "Noncanonical custom face suffix; explicitly remap the code and its references before importing"

localArt :: Text -> Either Text ()
localArt raw=unless (T.length raw==71 && "sha256:" `T.isPrefixOf` raw && T.all (`elem` ("0123456789abcdef"::String)) (T.drop 7 raw)) $
  Left "Author art requires a local sha256 object reference; no remote/file/data URI"

literalDependencies :: CustomCard -> Either Text [CardCode]
literalDependencies card=fmap concat $ for ["_signatures","_setAside"] \key->case Map.lookup key (cdMeta $ customCardDef card) of
  Nothing->Right []
  Just raw->do
    codes<-first T.pack $ Aeson.parseEither parseJSON raw
    unless (length codes<=100) $ Left "Too many literal author dependencies"
    for_ codes \code->when (isCustomCardCode code) $ canonicalCode code
    pure codes

validatePackage :: Text -> Text -> Either Text AuthorPackage
validatePackage raw expected=do
  unless (BS.length (TE.encodeUtf8 raw)<=8388608) $ Left "Author package exceeds8MiB"
  unless (rawDigest raw==expected) $ Left "Author package digest differs from the exact submitted UTF8 text"
  validateJsonProperties $ TE.encodeUtf8 raw
  value<-first T.pack $ eitherDecode $ BL.fromStrict $ TE.encodeUtf8 raw
  boundedDepth 0 value
  entries<-first T.pack $ Aeson.parseEither (withObject "native author package" \o->do
    format<-o .: "format";version<-o .: "version" :: Aeson.Parser Int
    unless (format==("arkham-author-package"::Text)&&version==1) $ fail "Unsupported author package format"
    o .: "cards" :: Aeson.Parser [Value]) value
  unless (not (null entries)&&length entries<=500) $ Left "Author package requires1to500cards"
  cards<-for entries \entry->do
    card<-first T.pack $ Aeson.parseEither parseJSON entry :: Either Text CustomCard
    let def=customCardDef card;code=cdCardCode def
    canonicalCode code
    unless (cdCardType def `elem` supportedTypes) $ Left "No original custom entity builder for this card type"
    unless (not (T.null $ toTitle def)&&T.length (toTitle def)<=300) $ Left "Author card title is empty or too long"
    for_ (customCardArt card) localArt
    for_ ["backArt","portrait","portraitBack"] \key->for_ (Map.lookup key $ cdMeta def) \art->case art of
      Null->Right ()
      String text->localArt text
      _->Left "Original custom image slots require local text references"
    -- Original DebugRegisterCustomCard stamps precisely this art identity.
    pure card {customCardDef=def {cdArt=unCardCode code}}
  unless (Map.size (Map.fromList [(cdCardCode $ customCardDef card,())|card<-cards])==length cards) $ Left "Duplicate primary custom card code"
  aliases<-foldM (\seen card->foldM (\m code->do
    canonicalCode code
    case Map.lookup code m of
      Just old | toJSON old==toJSON card->Right m
      Just _->Left "Conflicting custom primary code or ArkhamBuild alias in the package"
      Nothing->Right $ Map.insert code card m) seen (customCardCodes card)) mempty cards
  for_ cards \card->do
    dependencies<-literalDependencies card
    for_ dependencies \code->if isCustomCardCode code
      then unless (Map.member code aliases) $ Left $ "Missing custom signature/setAside dependency: "<>unCardCode code
      else unless (isJust $ lookupCardDef code) $ Left $ "Unknown official dependency: "<>unCardCode code
  pure $ AuthorPackage raw expected aliases (ignoredFields value entries)

packageCards :: AuthorPackage -> [CustomCard]
packageCards=Map.elems . Map.fromList . map (\card->(cdCardCode $ customCardDef card,card)) . Map.elems . authorCards

-- All accepted aliases are embedded with complete definitions, not merely the
-- subset found by the original deck helper. Same-code art differences conflict.
mergePackage :: Map CardCode CustomCard -> AuthorPackage -> Either Text (Map CardCode CustomCard)
mergePackage existing package=do
  let expanded=Map.fromList [(alias,card)|card<-Map.elems existing,alias<-customCardCodes card]
  for_ (Map.toList $ authorCards package) \(code,card)->for_ (Map.lookup code expanded) \old->
    unless (toJSON old==toJSON card) $ Left $ "An installed primary code or alias already has a different definition/art: "<>unCardCode code
  pure $ authorCards package<>existing

packageView :: AuthorPackage -> Value
packageView package=object
  ["packageSha256" .= authorDigest package,"structuralStatus" .= ("accepted"::Text)
  ,"rawPreservedInLibrary" .= True,"runtimeDefinition" .= ("Original typed CustomCard/CardDef; art identity normalized to primary code"::Text)
  ,"ignoredRuntimeFieldPaths" .= authorIgnoredFields package,"runtimeUnverified" .= True
  ,"staticDiagnostics" .= ["Only typed CardDef, canonical identities, original art slots and explicit signature/setAside closure are checked; dynamic expressions/messages remain original runtime behavior"::Text]
  ,"installableUnderTrustedAuthorMode" .= True,"cards" .= packageCards package]

authorSchema :: Value
authorSchema=object ["schemaVersion" .= (1::Int),"schema" .= Schema.customSchema,
  "supportedCardTypes" .= supportedTypes,"runtimeUnverified" .= True,
  "trust" .= ("Explicit local executable game content, not a sandbox"::Text)]
