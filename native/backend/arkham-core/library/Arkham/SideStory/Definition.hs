module Arkham.SideStory.Definition where

import Arkham.Prelude
import Arkham.Id
import Arkham.Card.CardCode
import Arkham.Homebrew.SideStories (allHomebrewSideStories)
import Arkham.Homebrew.SideStoryDefs (HomebrewSideStoryDef (..))

-- Engine-independent registry. Do not import Homebrew.Registry here: its
-- scenario implementations import Scenario.Runner, which imports SideStory.
data SideStoryDefinition = SideStoryDefinition
  { sideStoryId :: ScenarioId
  , sideStoryCost :: Int
  , sideStoryInvestigator :: Maybe Text
  , sideStoryParallelContent :: Bool
  , sideStoryNotAfter :: [ScenarioId]
  }
  deriving stock (Show, Eq)

sideStoryDefinitions :: [SideStoryDefinition]
sideStoryDefinitions = knownDefinitions <>
  [ ordinary (hsScenario definition) (hsCost definition)
  | definition <- allHomebrewSideStories
  , hsScenario definition `notElem` map sideStoryId knownDefinitions
  ]
 where
  knownDefinitions =
    [ ordinary "81001" 1, ordinary "82001" 3
    , (ordinary "83001" 2) {sideStoryNotAfter = ["83016"]}
    , ordinary "83016" 2, ordinary "85001" 2, ordinary "86001" 3
    , ordinary "87001" 2, ordinary "70001" 2, ordinary "84001" 3
    , ordinary "88001" 3, ordinary "71001" 2, ordinary "72001" 3, ordinary "73001" 2
    , challenge "90004" "Daisy Walker", challenge "90011" "\"Skids\" O'Toole"
    , challenge "90020" "Agnes Baker", challenge "90032" "Roland Banks"
    , challenge "90041" "Wendy Adams", challenge "90054" "Jim Culver"
    , challenge "90065" "Monterey Jack"
    , (ordinary "90094" 2) {sideStoryParallelContent = True}
    -- Existing definitions retain challenge-investigator admission metadata.
    , ordinary ":against-the-wendigo:001" 2
    , ordinary ":consternation-on-the-constellation:001" 2
    , ordinary ":the-symphony-of-erich-zann:001" 2
    ]
  ordinary sid cost = SideStoryDefinition sid cost Nothing False []
  challenge sid title = (ordinary sid 3) {sideStoryInvestigator = Just title}

sideStoryDefinition :: ScenarioId -> Maybe SideStoryDefinition
sideStoryDefinition sid = find ((== sid) . sideStoryId) sideStoryDefinitions

-- Same finite content eligibility as the pinned Vue deckRestrictions.ts.
-- Ordinary fronts of these investigators qualify, not just parallel fronts.
hasSideStoryParallelContent :: CardCode -> Bool
hasSideStoryParallelContent code = unCardCode code `elem`
  [ "01001","01501","90024","98004","01002","01502","90001"
  , "01003","01503","90008","01004","01504","90017"
  , "01005","01505","90037","02001","90059","02002","90078"
  , "02003","90084","98001","02004","90049","02005","90046"
  , "03006","90087","04004","90081","08007","90062" ]

-- Both the real runner and native admission/display use this distribution.
-- Callers obtain signature IDs with the original InvestigatorWithTitle query.
sideStoryPayments :: Ord iid => SideStoryDefinition -> Int -> [iid] -> [iid] -> Either Text [(iid,Int)]
sideStoryPayments definition effectiveCost signatures investigators = do
  when (effectiveCost < 0) $ Left "Invalid negative side-story entry cost"
  when (null investigators) $ Left "No investigators can enter a side story"
  case sideStoryInvestigator definition of
    Nothing -> pure [(iid,effectiveCost) | iid <- investigators]
    Just _ -> do
      when (null signatures) $ Left "The side story requires its challenge investigator"
      pure [(iid,if iid `elem` signatures then effectiveCost else 1) | iid <- investigators]
