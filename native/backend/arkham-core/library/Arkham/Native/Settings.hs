module Arkham.Native.Settings where

import Arkham.Prelude
import Arkham.Campaign.Option (CampaignOption (AddLitaChantler))
import Arkham.CampaignLogKey
import Arkham.Campaigns.NightOfTheZealot.Key qualified as N
import Arkham.CampaignStep qualified as CS
import Arkham.Card.CardCode (CardCode, unCardCode)
import Arkham.Game (Game (..))
import Arkham.Id
import Arkham.Message
import Arkham.Native.Answer
import Data.Aeson.Types qualified as Aeson
import Data.Map.Strict qualified as Map
import Data.Text qualified as T

-- One authoritative typed definition drives validation AND native UI metadata.
-- Unsupported contexts are not equivalent to an explicitly empty schema.
data SettingRow
  = BooleanKey Text CampaignLogKey
  | KeyChoice Text [CampaignLogKey]
  | BooleanOption Text CampaignOption
  | CrossedCards Text CampaignLogKey [(CardCode,Text)]

key :: N.NightOfTheZealotKey -> CampaignLogKey
key = NightOfTheZealotKey

standaloneRows :: ScenarioId -> Either Text [SettingRow]
standaloneRows scenario
  | exact == "01104" = Right []
  | exact == "01120" = Right
      [ BooleanKey "莉塔被迫寻找其他人帮助她的事业" $ key N.LitaWasForcedToFindOthersToHelpHerCause
      , KeyChoice "你的房屋" $ map key [N.YourHouseHasBurnedToTheGround,N.YourHouseIsStillStanding]
      , BooleanKey "食尸鬼祭司仍然活着" $ key N.GhoulPriestIsStillAlive
      , BooleanOption "将莉塔·钱德勒加入牌组" AddLitaChantler
      ]
  | exact == "01142" = Right
      [ BooleanKey "已过午夜" $ key N.ItIsPastMidnight
      , BooleanKey "食尸鬼祭司仍然活着" $ key N.GhoulPriestIsStillAlive
      , CrossedCards "逃脱的异教徒（勾选表示已划掉）" (key N.CultistsWhoGotAway)
          [("01137","“狼人”德鲁"),("01138","赫尔曼·柯林斯"),("01139","彼得·沃伦"),("01140","维多利亚·德弗罗"),("01141","露丝·特纳"),("01121b","蒙面猎人")]
      , BooleanOption "将莉塔·钱德勒加入牌组" AddLitaChantler
      ]
  | otherwise = Left "Advanced settings for this scenario are not implemented yet"
 where
  exact = unCardCode $ unScenarioId scenario

standaloneContext :: Game -> Either Text [SettingRow]
standaloneContext game = case gameMode game of
  That scenario -> standaloneRows scenario.id
  These _ scenario -> standaloneRows scenario.id
  _ -> Left "No scenario settings context"

campaignContext :: Game -> Either Text [SettingRow]
campaignContext game = case gameMode game of
  This campaign -> forCampaign campaign.id campaign.step
  These campaign _ -> forCampaign campaign.id campaign.step
  _ -> Left "No campaign settings context"
 where
  -- The original NOTZ campaign metadata explicitly has no campaign log form.
  -- Bind to known real starting steps; do not infer Return-To or unknown IDs.
  forCampaign cid step = case step of
    CS.ScenarioStep sid | cid == "01" && unCardCode (unScenarioId sid) `elem` ["01104","01120","01142"] -> Right []
    _ -> Left "Advanced settings for this campaign starting step are not implemented yet"

rowJSON :: SettingRow -> Value
rowJSON = \case
  BooleanKey label k -> object ["kind" .= ("toggle" :: Text),"label" .= label,"type" .= ("ToggleKey" :: Text),"key" .= k,"default" .= False]
  KeyChoice label choices -> object ["kind" .= ("pick" :: Text),"label" .= label,"type" .= ("PickKey" :: Text),"choices" .= choices,"choiceLabels" .= (["已烧毁","仍然矗立"] :: [Text]),"default" .= listToMaybe choices]
  BooleanOption label option -> object ["kind" .= ("toggle" :: Text),"label" .= label,"type" .= ("ToggleOption" :: Text),"key" .= option,"default" .= False]
  CrossedCards label k cards -> object ["kind" .= ("records" :: Text),"label" .= label,"type" .= ("ToggleCrossedOut" :: Text),"key" .= k,"recordable" .= ("RecordableCardCode" :: Text),"entries" .= [object ["key" .= c,"label" .= name,"default" .= False] | (c,name) <- cards]]

settingsView :: Game -> PlayerId -> Value
settingsView game player = case unwrapQuestion <$> Map.lookup player (gameQuestion game) of
  Just PickScenarioSettings -> result "StandaloneSettingsAnswer" $ standaloneContext game
  Just PickCampaignSettings -> result "CampaignSettingsAnswer" $ campaignContext game
  _ -> Null
 where
  result tag rows = case rows of
    Left reason -> object ["schemaVersion" .= (1 :: Int),"supported" .= False,"reason" .= reason]
    Right fields -> object ["schemaVersion" .= (1 :: Int),"supported" .= True,"answerTag" .= (tag :: Text),"rows" .= map rowJSON fields]

validateStandaloneSettings :: Game -> [StandaloneSetting] -> Either Text ()
validateStandaloneSettings game submitted = do
  rows <- standaloneContext game
  unless (length rows == length submitted) $ Left "Incomplete or extra standalone settings"
  sequence_ $ zipWith matchRow rows submitted

matchRow :: SettingRow -> StandaloneSetting -> Either Text ()
matchRow row submitted = case (row,submitted) of
  (BooleanKey _ expected,SetKey actual _) | expected == actual -> pure ()
  (KeyChoice _ allowed,SetKey selected True) | selected `elem` allowed -> pure ()
  (BooleanOption _ expected,SetOption actual _) | expected == actual -> pure ()
  (CrossedCards _ expected cards,SetRecorded actual (SomeRecordableType RecordableCardCode) entries) | expected == actual -> do
    selected <- for entries \case
      SetAsRecorded value -> card value
      SetAsCrossedOut value -> card value
      DoNotRecord _ -> Left "Crossed-card rows must explicitly record or cross out every offered card"
    unless (sort selected == sort (map (toJSON . fst) cards)) $ Left "Missing, duplicate, or foreign recorded card"
  _ -> Left "Setting does not match its offered row"
 where
  -- CardCode Eq intentionally normalizes card faces; compare canonical JSON
  -- instead so e.g. a back face cannot stand in for a different offered entry.
  card value = toJSON <$> (first T.pack (Aeson.parseEither parseJSON value) :: Either Text CardCode)

validateCampaignSettings :: Game -> CampaignSettings -> Either Text ()
validateCampaignSettings game settings = do
  rows <- campaignContext game
  unless (null rows && null settings.keys && null settings.counts && null settings.sets && null settings.options) $ Left "Campaign settings do not match the offered empty schema"
