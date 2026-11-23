module NativeNetworkSpec (networkSpecs) where

import Arkham.Prelude hiding (assert)
import Arkham.Game (Game (..))
import Arkham.Id (PlayerId (..))
import Arkham.Native.Network.View
import Arkham.Native.Network.Question
import Arkham.Native.Session qualified as Core
import Arkham.Native.Answer (Answer (DeckListAnswer, StandaloneSettingsAnswer), unwrapQuestion)
import Arkham.Native.Epic.Engine qualified as Epic
import Arkham.Native.Epic.Types qualified as Epic
import Arkham.Decklist.Type (ArkhamDBDecklist)
import Arkham.Question
import Control.Exception qualified as E
import Control.Monad.Trans.Except (runExceptT)
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Aeson.Key qualified as K
import Data.ByteString.Lazy qualified as BL
import Data.Map.Strict qualified as Map
import Data.UUID qualified as UUID
import Data.Text qualified as T
import Data.Either (isLeft)
import Prelude qualified as P
import System.Exit (exitFailure)

assert :: Text -> Bool -> IO ()
assert label ok = if ok then putStrLn ("PASS " <> label)
  else putStrLn ("FAIL " <> label) >> exitFailure

right :: Either Text a -> IO a
right = either (\e -> putStrLn e >> exitFailure) pure

field :: Text -> Value -> Value
field key (Object o) = fromMaybe Null $ KM.lookup (K.fromText key) o
field _ _ = Null

typed :: FromJSON a => Value -> IO a
typed = either (\e -> P.putStrLn e >> exitFailure) pure . Aeson.parseEither parseJSON

setField :: Text -> Value -> Value -> Value
setField key value (Object o) = Object $ KM.insert (K.fromText key) value o
setField _ _ _ = error "Expected object in controlled network fixture"

array :: Value -> [Value]
array (Array xs) = toList xs
array _ = error "Expected array in controlled network fixture"

-- Finite projection contracts, not a rule suite or an authorization substitute.
networkSpecs :: IO ()
networkSpecs = do
  let uuid=fromJustNote "network fixture UUID" . UUID.fromString
      player=PlayerId $ uuid "00000000-0000-4000-8000-000000000101"
      other=PlayerId $ uuid "00000000-0000-4000-8000-000000000102"
      stranger=PlayerId $ uuid "00000000-0000-4000-8000-000000000103"
      secret="PRIVATE_EXECUTION_SENTINEL"::Text
      safe q=questionView (const Null) (q :: Question Text)
      absent value=not $ secret `T.isInfixOf` decodeUtf8 (BL.toStrict $ encode value)
      choice label=Label label [secret]
  request <- either (\e -> P.putStrLn e >> exitFailure) pure $ Aeson.parseEither parseJSON $
    object ["scenario" .= ("01104"::Text),"players" .= [player,other],"seed" .= (123::Int)]
  (snapshot,_) <- Core.createSession request >>= right
  let game=Core.snapshotGame snapshot
  view <- right $ networkView game player
  assert "network rejects unassigned recipient" $ isLeft $ networkView game stranger
  assert "network independent view omits raw transport and hotseat fields" $ case view of
    Object o -> all (not . (`KM.member` o))
      ["queue","seed","game","mode","cards","authorCards","debugSchema","snapshot"]
    _ -> False
  let otherOnly=game {gameQuestion=Map.delete player $ gameQuestion game}
  privateView <- right $ networkView otherOnly player
  assert "network other seat question is not delivered" $ field "question" privateView==Null
  assert "network ordinary choices retain indices but remove execution payloads" $
    let q=safe $ ChooseOne [choice "first",Info mempty,choice "third"]
    in absent q && case field "choices" q of
      Array xs -> map (field "index") (toList xs)==map toJSON ([0,1,2]::[Int])
        && field "selectable" (headEx $ drop 1 $ toList xs)==Bool False
      _ -> False
  let payment=PaymentAmountChoice (uuid "00000000-0000-4000-8000-000000000104")
        (fromString "01001") 0 2 "Pay" secret
  assert "network payment and dropdown never serialize messages" $
    absent (safe $ ChoosePaymentAmounts "pay" (Just $ TotalAmountTarget 2) [payment])
    && absent (safe $ DropDown [("first",secret)])
  assert "network wizard read and nested wrappers remove all execution messages" $
    absent (safe $ ChooseOneWizard mempty [WizardChoice "wizard" mempty [secret]] "ok" "back")
    && absent (safe $ QuestionLabel "header" Nothing $ Read mempty (BasicReadChoices [choice "continue"]) Nothing)
  assert "network automatic batch index includes synthetic original zero" $
    case field "choices" (safe $ ChooseOneAtATimeWithAuto "all" [choice "one",choice "two"]) of
      Array xs -> map (field "index") (toList xs)==map toJSON ([0,1,2]::[Int])
      _ -> False
  gameTarget <- typed $ object ["tag" .= ("GameTarget"::Text)]
  assert "network amount ranges are preserved without original target" $
    let q=safe $ ChooseAmounts "amount" (TotalAmountTarget 2)
          [AmountChoice (uuid "00000000-0000-4000-8000-000000000105") "A" 0 2] gameTarget
    in field "target" q==toJSON (TotalAmountTarget 2) && case field "choices" q of
      Array xs -> field "max" (headEx $ toList xs)==toJSON (2::Int)
      _ -> False
  assert "network arbitrary specific details do not cross boundary" $
    absent $ safe $ PickScenarioSpecific "unknown" $ object ["secret" .= secret]
  flavor <- typed $ object ["title" .= Null,"body" .=
    [object ["tag" .= ("I18nEntry"::Text),"key" .= ("display"::Text)
      ,"variables" .= object ["unsafe" .= object ["payload" .= secret]]]]]
  assert "network nested flavor variable payload is not transported" $ absent $ flavorView flavor
  assert "network from-each choice indices preserve original concat offsets" $
    case field "groups" (safe $ ChooseOneFromEach [[choice "one",choice "two"],[choice "three"]]) of
      Array xs -> map (map (field "index") . array) (toList xs)==map (map toJSON) ([[0,1],[2]]::[[Int]])
      _ -> False
  publicSource <- typed $ object ["tag" .= ("GameSource"::Text)]
  privateSource <- typed $ object ["tag" .= ("CardCodeSource"::Text),"contents" .= secret]
  token <- typed $ String "Resource"
  assert "network exchange preserves public source but omits private source contents" $
    let exchange source=safe $ ChooseExchangeAmounts source "01001" 2 "01002" 1 token
    in field "source" (exchange publicSource)==toJSON publicSource
      && field "sourceSupported" (exchange publicSource)==Bool True
      && field "source" (exchange privateSource)==Null && absent (exchange privateSource)

  -- New finite display adapter only. Original Cost/Message rule semantics are
  -- not re-tested; fixtures exercise redaction, shape and offered index routing.
  let atom tag n=object ["tag" .= (tag::Text),"contents" .= (n::Int)]
      freeCost=object ["tag" .= ("Free"::Text)]
      costJSON ref raw=costView ref <$> typed raw
  freeDisplay <- costJSON (const Null) freeCost
  resourceDisplay <- costJSON (const Null) $ atom "ResourceCost" 2
  assert "network finite free and resource cost display retains exact integer" $
    field "kind" freeDisplay==String "Free" && field "amount" resourceDisplay==toJSON (2::Int)
  clueDisplay <- costJSON (const Null) $ object ["tag" .= ("ClueCost"::Text),"contents" .= atom "PerPlayer" 3]
  assert "network finite per-player cost stays a display formula not guessed scalar" $
    field "kind" clueDisplay==String "Variable" && field "amount" clueDisplay==Null
  unsupportedDisplay <- costJSON (const Null) $ object ["tag" .= ("LabeledCost"::Text)
    ,"contents" .= toJSON [String secret,object ["tag" .= ("SourcedCost"::Text),"contents" .=
      toJSON [toJSON privateSource,object ["tag" .= ("GloriaCost"::Text)]]]]]
  assert "network finite cost omits authored private labels sources and unknown internals" $
    absent unsupportedDisplay && field "kind" unsupportedDisplay==String "Unsupported"
  groupDisplay <- costJSON (const Null) $ object ["tag" .= ("Costs"::Text),"contents" .=
    [atom "ActionCost" 1,object ["tag" .= ("OrCost"::Text),"contents" .=
      [atom "ResourceCost" 2,object ["tag" .= ("GloriaCost"::Text)]]]]]
  assert "network finite nested AND OR retains partial children and incomplete status" $
    field "kind" groupDisplay==String "All" && field "supported" groupDisplay==Bool False
    && field "kind" (headEx $ drop 1 $ array $ field "items" groupDisplay)==String "Any"
  let exhaust=object ["tag" .= ("ExhaustCost"::Text),"contents" .= toJSON gameTarget]
      publicRef=object ["kind" .= ("Asset"::Text),"id" .= ("already-public"::Text)]
  hiddenCost <- costJSON (const Null) exhaust
  publicCost <- costJSON (const publicRef) exhaust
  assert "network finite cost target uses only authorized public resolver" $
    field "kind" hiddenCost==String "Unsupported" && field "target" hiddenCost==Null
    && field "target" publicCost==publicRef
  bounded <- costJSON (const Null) $ object ["tag" .= ("Costs"::Text),"contents" .= (replicate 257 freeCost :: [Value])]
  assert "network finite cost node budget fails closed without huge subtree" $
    field "kind" bounded==String "Unsupported"
  typedCost <- typed $ atom "ResourceCost" 4
  assert "network finite cost wrappers retain offered index and omit messages" $
    let wrapped=safe $ PayCostQuestion typedCost $ ChooseOne [CostLabel typedCost [secret]]
        offered=headEx $ array $ field "choices" $ field "question" wrapped
    in absent wrapped && field "amount" (field "cost" wrapped)==toJSON (4::Int)
      && field "amount" (field "cost" offered)==toJSON (4::Int) && field "index" offered==toJSON (0::Int)
  conditional <- costJSON (const Null) $ object ["tag" .= ("OptionalCost"::Text),"contents" .= atom "ResourceCost" 1]
  assert "network finite optional cost is not flattened into mandatory payment" $
    field "kind" conditional==String "Unsupported"

  -- Real two-seat deck installation, stopping as soon as both original
  -- investigator implementations exist. No scenario completion is invented.
  decks <- eitherDecodeFileStrict @[ArkhamDBDecklist] "arkham-core/data/community-starter-decks.json"
    >>= either (\e -> P.putStrLn e >> exitFailure) pure
  let deckFor pid=if pid==player then headEx decks else headEx $ drop 1 decks
      actorValues s=case field "investigators" $ toJSON $ gameEntities $ Core.snapshotGame s of
        Object o -> KM.elems o
        _ -> error "Missing actual investigator entities"
      install budget s
        | length (actorValues s)==2 = pure s
        | budget <= (0::Int) = putStrLn "FAIL real network deck installation did not finish" >> exitFailure
        | otherwise = case [(pid,q) | (pid,raw)<-Map.toList $ gameQuestion $ Core.snapshotGame s
                          ,let q=unwrapQuestion raw,q==ChooseDeck || q==PickScenarioSettings] of
            (pid,q):_ -> do
              let answer=if q==ChooseDeck then DeckListAnswer (deckFor pid) pid else StandaloneSettingsAnswer []
              (next,_) <- Core.applyAnswer s pid (Core.snapshotRevision s) answer >>= right
              install (budget-1) next
            [] -> putStrLn "FAIL no actual deck/settings recipient before both investigators" >> exitFailure
  installed <- install 5 snapshot
  assert "network canary fixture uses two real installed investigators" $ length (actorValues installed)==2
  beforeNoTest <- E.evaluate $ BL.length $ Core.exportSnapshot installed
  noTest <- Core.networkSkillTestView (Core.snapshotGame installed) player
  assert "network absent skill test projects null without changing snapshot" $
    noTest==Null && BL.length (Core.exportSnapshot installed)==beforeNoTest
  let testInvestigator=field "id" $ headEx $ actorValues installed
      token face=object ["chaosTokenId" .= ("00000000-0000-4000-8000-000000008002"::Text)
        ,"chaosTokenFace" .= (face::Text),"chaosTokenRevealedBy" .= Null
        ,"chaosTokenCancelled" .= False,"chaosTokenSealed" .= False]
      test=object ["id" .= ("00000000-0000-4000-8000-000000008001"::Text)
        ,"investigator" .= testInvestigator,"resolveFailureInvestigator" .= testInvestigator
        ,"type" .= object ["tag" .= ("SkillSkillTest"::Text),"contents" .= ("SkillIntellect"::Text)]
        ,"baseValue" .= object ["tag" .= ("SkillBaseValue"::Text),"contents" .= ("SkillIntellect"::Text)]
        ,"difficulty" .= (2::Int),"setAsideChaosTokens" .= [token "ElderSign"]
        ,"revealedChaosTokens" .= [token "Skull"],"resolvedChaosTokens" .= ([]::[Value])
        ,"toResolveChaosTokens" .= ([]::[Value]),"result" .= object ["tag" .= ("Unrun"::Text)]
        ,"committedCards" .= object [],"source" .= privateSource,"target" .= toJSON gameTarget
        ,"action" .= Null,"subscribers" .= ([]::[Value]),"isRevelation" .= False,"iconValues" .= ([]::[Value])]
  testGame <- typed @Game $ setField "gameSkillTest" test $ toJSON $ Core.snapshotGame installed
  let testBytes=encode testGame
  testProjection <- Core.networkSkillTestView testGame player
  assert "network effective skill display excludes private tokens source and runtime state" $
    field "modifiedDifficulty" testProjection==toJSON (2::Int)
    && field "skills" testProjection==toJSON (["SkillIntellect"]::[Text])
    && field "revealedChaosTokens" testProjection==toJSON [object ["chaosTokenFace" .= ("Skull"::Text),"cancelled" .= False]]
    && field "modifiedSkillValue" testProjection/=Null && absent testProjection
    && all (\key -> field key testProjection==Null) ["source","setAsideChaosTokens","toResolveChaosTokens","modifiers","queue","seed"]
    && encode testGame==testBytes
  let ownId="00000000-0000-4000-8000-000000009001"::Text
      otherId="00000000-0000-4000-8000-000000009002"::Text
      ownDeckIds=["00000000-0000-4000-8000-000000009003","00000000-0000-4000-8000-000000009004"]::[Text]
      otherDeckIds=["00000000-0000-4000-8000-000000009005","00000000-0000-4000-8000-000000009006"]::[Text]
      isOwn actor=field "playerId" actor==toJSON player
      inject reverseDeck actor=
        let card=headEx $ array $ field "deck" actor
            handId=if isOwn actor then ownId else otherId
            ids=if isOwn actor then ownDeckIds else otherDeckIds
            deck=map (\ident -> setField "id" (String ident) card) $ if reverseDeck then reverse ids else ids
            hand=object ["tag" .= ("PlayerCard"::Text),"contents" .= setField "id" (String handId) card]
        in setField "hand" (toJSON [hand]) $ setField "deck" (toJSON deck) actor
      injectGame reverseDeck=
        let raw=toJSON $ Core.snapshotGame installed
            entities=field "gameEntities" raw
            actors=case field "investigators" entities of Object o -> Object $ fmap (inject reverseDeck) o; _ -> Null
        in setField "gameEntities" (setField "investigators" actors entities) raw
  canaryGame <- typed @Game $ injectGame False
  reorderedGame <- typed @Game $ injectGame True
  let canarySnapshot=installed {Core.snapshotGame=canaryGame}
      beforeBytes=Core.exportSnapshot canarySnapshot
  _ <- E.evaluate $ BL.length beforeBytes
  canary <- right $ networkView canaryGame player
  _ <- E.evaluate $ BL.length $ encode canary
  reverseView <- right $ networkView reorderedGame player
  let text=decodeUtf8 $ BL.toStrict $ encode canary
      boardActors=array $ field "investigators" $ field "board" canary
      ownViews=array $ field "own" canary
  assert "network own visible hand card survives projection" $ ownId `T.isInfixOf` text
  assert "network other hand and all deck instance IDs are absent" $
    all (\ident -> not $ ident `T.isInfixOf` text) $ otherId:ownDeckIds<>otherDeckIds
  assert "network private hand and deck counts remain correct" $
    length boardActors==2 && all (\actor -> field "handCount" actor==toJSON (1::Int)
      && field "deckCount" actor==toJSON (2::Int)) boardActors
      && length ownViews==1 && field "deckCount" (headEx ownViews)==toJSON (2::Int)
  assert "network reordered private decks yield identical projected bytes" $
    encode canaryGame/=encode reorderedGame && encode canary==encode reverseView
  assert "network projection preserves preforced complete checkpoint bytes" $
    beforeBytes==Core.exportSnapshot canarySnapshot

  ownTarget <- typed $ object ["tag" .= ("CardIdTarget"::Text),"contents" .= ownId]
  otherTarget <- typed $ object ["tag" .= ("CardIdTarget"::Text),"contents" .= otherId]
  let offeredGame=canaryGame {gameQuestion=Map.singleton player $ ChooseOne
        [TargetLabel ownTarget [],TargetLabel otherTarget []]}
      offeredBefore=encode offeredGame
  (offeredRefs,extras) <- Core.networkExtrasView Nothing offeredGame player False
  let offeredRows=array $ field "choiceCards" extras
      extrasText=decodeUtf8 $ BL.toStrict $ encode extras
  assert "network choice references are actual offered owned faces only" $
    length offeredRows==1 && field "cardId" (headEx offeredRows)==String ownId
      && not (otherId `T.isInfixOf` extrasText) && length offeredRefs==1
  let noOffer=offeredGame {gameQuestion=Map.singleton player $ ChooseOne [Label "not a card" []]}
  (noRefs,noExtras) <- Core.networkExtrasView Nothing noOffer player False
  assert "network unoffered candidate faces are not projected" $
    null noRefs && null (array $ field "choiceCards" noExtras)
  assert "network choice inspection leaves exact original game queue RNG input" $
    encode offeredGame==offeredBefore && beforeBytes==Core.exportSnapshot canarySnapshot
  let privateIds=otherId:ownDeckIds<>otherDeckIds
  assert "network choice detail never looks up other hands or deck instances" $
    all (\ident -> not $ ident `T.isInfixOf` extrasText) privateIds
  extended <- right $ networkViewWithCards offeredRefs offeredGame player
  assert "network offered reference bridge keeps original indexes and rejects unknown target" $
    let rows=array $ field "choices" $ field "question" extended
    in map (field "index") rows==map toJSON ([0,1]::[Int])
      && field "target" (headEx rows)/=Null && field "target" (headEx $ drop 1 rows)==Null
  let eid=uuid "00000000-0000-4000-8000-000000007500"
      g1=uuid "00000000-0000-4000-8000-000000007501"
      g2=uuid "00000000-0000-4000-8000-000000007502"
  epicRequest <- typed @Epic.EpicCreate $ object
    ["eventId" .= eid,"definition" .= object ["scenarioId" .= ("85001"::Text),"difficulty" .= ("Standard"::Text)
      ,"tarot" .= False,"blobElse" .= False,"timeLimitMinutes" .= (180::Int)]
    ,"groups" .= [object ["gameId" .= g1,"ordinal" .= (0::Int),"name" .= ("Network A"::Text),"players" .= [player],"seed" .= (85001::Int)]
                 ,object ["gameId" .= g2,"ordinal" .= (1::Int),"name" .= ("Network B"::Text),"players" .= [other],"seed" .= (85002::Int)]]]
  (epic,_) <- Epic.createEvent epicRequest >>= right
  epicGroup <- right $ Epic.networkPlayerGroup epic player
  assert "network Epic membership resolves unique latest group and rejects strangers" $
    Epic.groupGameId epicGroup==g1 && isLeft (Epic.networkPlayerGroup epic stranger)
  let epicBefore=encode epic
      summary=Epic.networkSharedSummary epic
  ctx <- runExceptT (Epic.newContext epic epicGroup) >>= right
  (_,epicExtras) <- Core.networkExtrasView (Just ctx) (Epic.groupGame $ Epic.groupSnapshot epicGroup) player True
  assert "network Epic original-context inspection preserves whole event and emits regions" $
    encode epic==epicBefore && field "zoneSchema" epicExtras/=Null
  assert "network Epic shared summary contains only eight finite public fields" $
    case summary of
      Object o -> sort (KM.keys o)==sort ["schemaVersion","totalGroups","totalInvestigators","countermeasures"
        ,"timeLimitMinutes","timerStartedAt","groupsReady","awaitingOrganizer"] && field "totalGroups" summary==toJSON (2::Int)
      _ -> False
