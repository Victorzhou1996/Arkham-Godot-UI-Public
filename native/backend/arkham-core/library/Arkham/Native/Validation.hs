module Arkham.Native.Validation where

import Arkham.Prelude
import Arkham.Game (Game (..), GameMode)
import Arkham.CampaignStep qualified as CS
import Arkham.Campaigns.TheScarletKeys.Meta (MapLocationId (..), greenLocations)
import Arkham.Id
import Arkham.Message
import Arkham.Target (Target (CardCodeTarget))
import Arkham.Native.Answer
import Arkham.Native.Settings (validateStandaloneSettings, validateCampaignSettings)
import Arkham.Tarot (TarotCard (..), TarotCardFacing (..), toTarotArcana)
import Data.Map.Strict qualified as Map
import Data.These (These (..))
import Data.Aeson.Types qualified as Aeson
import Data.Aeson.KeyMap qualified as KM
import Data.Text qualified as T
import Control.Monad.Fail (fail)

-- Match the resolver's routing, but reject states for which it would silently
-- handle no messages. Only the actual next step is a public native operation.
offeredContinuation :: GameMode -> Either Text CS.Continuation
offeredContinuation = \case
  This campaign -> campaignStep campaign.step
  That scenario -> maybe (Left "No scenario continuation") scenarioStep scenario.step
  These campaign scenario -> case scenario.step of
    Just (CS.ContinueCampaignStep continuation) -> Right continuation
    Just CS.ScenarioStepWithOptions {} -> Left "Scenario options have no offered continuation"
    _ -> campaignStep campaign.step
 where
  scenarioStep = \case
    CS.ContinueCampaignStep continuation -> Right continuation
    _ -> Left "No scenario continuation"
  campaignStep = \case
    CS.ContinueCampaignStep continuation -> Right continuation
    CS.StandaloneScenarioStep _ (CS.ContinueCampaignStep continuation) -> Right continuation
    _ -> Left "No campaign continuation"

validateContinuation :: GameMode -> CS.CampaignStep -> Either Text ()
validateContinuation mode submitted = do
  offered <- offeredContinuation mode
  -- CardCode's Eq is deliberately loose for some faces; protocol offers must
  -- match the whole serialized value, not a normalized or face-equivalent step.
  unless (toJSON submitted == toJSON offered.nextStep) $ Left "Campaign step is not the offered next step"

-- Validate the *question's* contract, not merely the shape of submitted JSON.
validateAnswer :: Game -> PlayerId -> Answer -> Either Text ()
validateAnswer _ _ SetTriggerModeAnswer {} = Left "Trigger preference requires the native read-only context"
validateAnswer _ _ AutoSkipTriggersAnswer {} = Left "Automatic trigger skip requires the native read-only context"
validateAnswer game player answer = do
  q <- maybe (Left "Player is not being asked") (Right . unwrapQuestion) $ Map.lookup player $ gameQuestion game
  case (answer, q) of
    (Raw {}, _) -> Left "Raw messages are not a public operation"
    (StandaloneSettingsAnswer settings, PickScenarioSettings) -> validateStandaloneSettings game settings
    (CampaignSettingsAnswer settings, PickCampaignSettings) -> validateCampaignSettings game settings
    (CampaignSpecificAnswer key value, PickCampaignSpecific offered details) -> validateCampaignSpecific offered details key value
    (ScenarioSpecificAnswer key value, PickScenarioSpecific offered details) -> validateScenarioSpecific offered details key value
    (DeckListAnswer deck _, ChooseDeck) -> validateLocalDeckSelection game player deck
    (DeckListAnswer deck _, ChooseUpgradeDeck) -> do
      iid <- upgradeInvestigator game player
      void $ validateUpgradeRequest game player $ UpgradeDeckRequest iid (Just deck)
    (UpgradeDeckAnswer request, ChooseUpgradeDeck) -> void $ validateUpgradeRequest game player request
    (ReplaceInvestigatorAnswer request, ChooseUpgradeDeck) -> void $ validateReplacementRequest game player request
    (DeckListAnswer deck _, ChooseJoinDeck {}) -> validateLocalDeckSelection game player deck
    (AmountsAnswer response, ChooseAmounts _ target choices _) ->
      validateAmounts (Just target) [(key,lo,hi) | AmountChoice key _ lo hi <- choices] $ arAmounts response
    (PaymentAmountsAnswer response, ChoosePaymentAmounts _ target choices) ->
      validateAmounts target [(key,max 0 lo,hi) | PaymentAmountChoice key _ lo hi _ _ <- choices] $ parAmounts response
    (ExchangeAmountsAnswer source from to token amount, ChooseExchangeAmounts expected a av b bv kind)
      | source == expected && from == a && to == b && token == kind
      , toInteger amount >= negate (toInteger bv), toInteger amount <= toInteger av -> pure ()
    (PickDestinyAnswer selected, PickDestiny offered) -> validateDestiny offered selected
    (RetireInvestigatorAnswer iid, ContinueCampaign) -> validateRetire game player iid
    (RejoinInvestigatorAnswer iid, ContinueCampaign) -> validateRejoin game player iid
    (JoinLocalCampaignAnswer request, ContinueCampaign) -> validateLocalJoin game player request
    (BeginUpgradeAnswer, ContinueCampaign) -> void $ offeredUpgrade game player
    (BeginSideStoryAnswer request, ContinueCampaign) -> void $ offeredSideStory game player request
    (ApplyOverlayAnswer iid overlay, ContinueCampaign) -> void $ validateLocalOverlay game player iid overlay
    (CampaignStepAnswer step, ContinueCampaign) -> validateContinuation (gameMode game) step >> validateSideStoryEntry game player step
    (CampaignStepAnswerFor recipient _ step, ContinueCampaign)
      | recipient == player -> validateContinuation (gameMode game) step >> validateSideStoryEntry game player step
    (OrderedAnswer response, ChooseOneAtATime choices)
      | sort (orChoices response) == [0 .. length choices - 1] -> validateBatch choices
    (OrderedAnswer response, ChooseOneAtATimeWithAuto _ choices)
      | sort (orChoices response) == [0 .. length choices - 1] -> validateBatch choices
    (Answer response, ChooseOneAtATimeWithAuto _ choices) | qrChoice response == 0 -> validateBatch choices
    (Answer response, _) -> case indexedChoices q of
      Just choices | qrChoice response >= 0, Just choice <- choices !!? qrChoice response ->
        validateActionable choice
      _ -> Left "Wrong question type or choice index"
    _ -> Left "Answer is not offered by this question"

-- Match the original PickDestiny.vue contract: fixed scenario/arcana pairs,
-- only facing may change, and ceil(n/2) cards must be Reversed. A multiset of
-- whole TarotCards was incorrect: it allowed swaps and rejected real rotations.
validateDestiny :: [DestinyDrawing] -> [DestinyDrawing] -> Either Text ()
validateDestiny offered selected = do
  let scopes = [scope | DestinyDrawing scope _ <- offered]
      pairs xs = [(scope,toTarotArcana card) | DestinyDrawing scope card <- xs]
      reversed = length [() | DestinyDrawing _ (TarotCard Reversed _) <- selected]
  unless (not (null offered) && length scopes == length (nub scopes)) $
    Left "Destiny offer requires nonempty unique scenarios"
  unless (length selected == length offered && sort (pairs selected) == sort (pairs offered)) $
    Left "Destiny must preserve each offered scenario and its tarot arcana"
  unless (reversed == (length offered + 1) `div` 2) $
    Left "Destiny requires exactly half the cards reversed, rounding up"

validateActionable :: UI Message -> Either Text ()
validateActionable = \case
  InvalidLabel {} -> Left "Choice is unavailable"
  Info {} -> Left "Information is not an actionable choice"
  _ -> pure ()

-- Whole-list resolution calls uiToRun for every entry. Do not silently drop
-- non-actionable entries, reinterpret them as Noop, or change original indexes.
validateBatch :: [UI Message] -> Either Text ()
validateBatch choices = do
  when (null choices) $ Left "No actionable batch is offered"
  traverse_ validateActionable choices

decodeSpecific :: FromJSON a => Value -> Either Text a
decodeSpecific = first T.pack . Aeson.parseEither parseJSON

parseEmbark :: Value -> Aeson.Parser (MapLocationId,[MapLocationId],[(MapLocationId,Maybe Int)],Bool)
parseEmbark = withObject "Embark offer" \o -> do
  current <- o .: "current"
  available <- o .: "available"
  entries <- o .: "locations"
  locations <- for entries \(location,details) -> do
    distance <- withObject "Embark location" (.: "travel") details
    pure (location,distance)
  ticket <- o .: "hasTicket"
  pure (current,available,locations,ticket)

validateCampaignSpecific :: Text -> Value -> Text -> Value -> Either Text ()
validateCampaignSpecific offered details operation value
  | offered /= "embark" = Left "Unsupported campaign-specific question"
  | otherwise = do
      routes <- embarkRoutes details
      destination <- decodeSpecific value
      unless (any (\(location,_,_,_,actions) -> location == destination && any (\(key,_,_) -> key == operation) actions) routes) $
        Left "Campaign operation is not offered for this destination"

-- A single finite action list drives both validation and read-only UI metadata.
-- The terminal offer contains distances from the previous location: do not
-- advertise those stale numbers as the final journey's executed cost.
type EmbarkRoute = (MapLocationId,Bool,Bool,Maybe Text,[(Text,Maybe Integer,Bool)])

embarkRoutes :: Value -> Either Text [EmbarkRoute]
embarkRoutes details = do
  (current,available,locations,ticket) <- first T.pack $ Aeson.parseEither parseEmbark details
  unless (length locations == length (nub $ map fst locations)) $ Left "Ambiguous embark locations"
  let terminal = current == Tunguska && available == [Tunguska]
  pure $ flip map locations \(location,distance) ->
    let here = current == location
        final = terminal && location == Tunguska
        effective = fmap (\days -> toInteger days + if location `elem` greenLocations then 1 else 0) distance
        reachable = maybe False (>= 0) distance
        actions
          | final = [("travel",Nothing,False)]
          | terminal || here || not reachable = []
          | otherwise = [("travel",effective,False) | location `elem` available]
              <> [("travelVia",effective,False)]
              <> [("travelWithTicket",Just 1,True) | location `elem` available && ticket && maybe False (> 1) effective]
        reason
          | final = Nothing
          | terminal = Just "finaleOnly"
          | here = Just "currentLocation"
          | not reachable = Just "unreachable"
          | location `notElem` available = Just "passOnly"
          | otherwise = Nothing
    in (location,here,final,reason,actions)

embarkMetadata :: Value -> Either Text Value
embarkMetadata details = do
  routes <- embarkRoutes details
  pure $ object
    [ "schemaVersion" .= (1 :: Int), "supported" .= True, "kind" .= ("embark" :: Text)
    , "answerTag" .= ("CampaignSpecificAnswer" :: Text)
    , "destinations" .= map (\(location,here,final,reason,actions) -> object
        [ "id" .= location, "nameKey" .= ("theScarletKeys.locations." <> tshow location <> ".name")
        , "isCurrent" .= here, "isFinale" .= final, "unavailableReasonCode" .= reason
        , "actions" .= map (\(key,days,ticket) -> object ["operation" .= key,"days" .= days,"consumesTicket" .= ticket]) actions
        ]) routes
    ]

parseSpiritOffer :: Value -> Aeson.Parser ([Text],Int,[Text])
parseSpiritOffer = withObject "Spirit deck offer" \o -> (,,) <$> o .: "cardCodes" <*> o .: "count" <*> o .: "fixed"

spiritOffer :: Value -> Either Text ([Text],Int,[Text])
spiritOffer details = do
  result@(candidates,count,fixed) <- first T.pack $ Aeson.parseEither parseSpiritOffer details
  unless (length candidates == length (nub candidates) && length fixed == length (nub fixed) && all (`notElem` fixed) candidates) $ Left "Ambiguous spirit deck offer"
  unless (count > 0 && count <= length candidates) $ Left "Invalid spirit deck size"
  pure result

spiritMetadata :: Value -> Either Text Value
spiritMetadata details = do
  (candidates,count,fixed) <- spiritOffer details
  pure $ object
    [ "schemaVersion" .= (1 :: Int), "supported" .= True, "kind" .= ("spiritDeck" :: Text)
    , "answerTag" .= ("ScenarioSpecificAnswer" :: Text), "operation" .= ("laidToRest.buildSpiritDeck" :: Text)
    , "cardCodes" .= candidates, "count" .= count, "fixed" .= fixed
    ]

interactionView :: Game -> PlayerId -> Value
interactionView game player = case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
  Just (PickCampaignSpecific "embark" details) -> project $ embarkMetadata details
  Just (PickScenarioSpecific "laidToRest.buildSpiritDeck" details) -> project $ spiritMetadata details
  Just PickCampaignSpecific {} -> unsupported "Unsupported campaign-specific question"
  Just PickScenarioSpecific {} -> unsupported "Unsupported scenario-specific question"
  _ -> Null
 where
  unsupported reason = object ["schemaVersion" .= (1 :: Int),"supported" .= False,"reason" .= (reason :: Text)]
  project = either unsupported id

validateScenarioSpecific :: Text -> Value -> Text -> Value -> Either Text ()
validateScenarioSpecific offered details operation value
  | offered /= "laidToRest.buildSpiritDeck" || operation /= offered = Left "Unsupported scenario-specific question"
  | otherwise = do
      (candidates,count,fixed) <- spiritOffer details
      submitted <- first T.pack $ Aeson.parseEither (withObject "Spirit deck answer" \o -> do
        unless (KM.keys o == ["cardCodes"]) $ fail "Only cardCodes is allowed"
        o .: "cardCodes") value
      unless (count > 0 && count <= length candidates && length submitted == count) $ Left "Wrong spirit deck size"
      unless (length submitted == length (nub submitted)) $ Left "Duplicate spirit deck card"
      unless (all (`elem` candidates) submitted && all (`notElem` fixed) submitted) $ Left "Spirit deck card is not offered"

validateAmounts :: Maybe AmountTarget -> [(UUID,Int,Int)] -> Map UUID Int -> Either Text ()
validateAmounts target ranges submitted = do
  let allowed = Map.fromList [(key,(lo,hi)) | (key,lo,hi) <- ranges]
  unless (length ranges == Map.size allowed) $ Left "Duplicate choice IDs in question"
  unless (all (`Map.member` allowed) $ Map.keys submitted) $ Left "Unknown amount choice"
  for_ ranges \(key,lo,hi) -> do
    let n = Map.findWithDefault 0 key submitted
    unless (n >= lo && n <= hi) $ Left "Amount outside offered range"
  let total = sum $ map toInteger $ Map.elems submitted
      matches = \case
        MinAmountTarget n -> total >= toInteger n
        MaxAmountTarget n -> total <= toInteger n
        TotalAmountTarget n -> total == toInteger n
        AmountOneOf ns -> total `elem` map toInteger ns
  unless (maybe True matches target) $ Left "Amounts do not satisfy total"

indexedChoices :: Question Message -> Maybe [UI Message]
indexedChoices = \case
  ChooseFromCollection iid source codes -> Just
    [CardLabel code False [HandleTargetChoice iid source (CardCodeTarget code)] | code <- codes]
  ChooseOne xs -> Just xs
  PlayerWindowChooseOne xs -> Just xs
  WindowChooseOne xs -> Just xs
  ChooseOneFromEach groups -> Just $ concat groups
  ChooseN _ xs -> Just xs
  ChooseSome xs -> Just xs
  ChooseSome1 _ xs -> Just xs
  ChooseUpToN _ xs -> Just xs
  ChooseOneAtATime xs -> Just xs
  ChooseOneAtATimeWithAuto label xs -> Just $ Label label [] : xs
  PickSupplies _ _ xs _ -> Just xs
  DropDown xs -> Just [Label label [message] | (label,message) <- xs]
  ChooseOneWizard _ xs _ _ -> Just [Label label messages | WizardChoice label _ messages <- xs]
  Read _ choices _ -> Just $ case choices of
    BasicReadChoices xs -> xs
    BasicReadChoicesN _ xs -> xs
    BasicReadChoicesUpToN _ xs -> xs
    LeadInvestigatorMustDecide xs -> xs
  _ -> Nothing
